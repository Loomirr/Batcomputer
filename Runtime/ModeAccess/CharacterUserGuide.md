# Batcomputer character-mode support

This native UE4SS helper applies the Normal / Mayhem / Both setting authored in Batcomputer. Character policies are supplied by each mod's registry plugin; this helper alone adds no characters or crossover shortcuts.

## Install

Close the game, back up saves, and install the complete character-mod release: PAK files, registry plugin and the included `Binaries/Win64/ue4ss/Mods/LOTDKModeAccess` folder. Keep only one shared helper installation. Requires compatible LOTDK UE4SS; Mayhem requires the owned and installed DLC.

The supported Steam executable profile is **1344350 (UE 5.6.1)**. Unknown builds are rejected; check `[LOTDKModeAccess]` in `UE4SS.log` after an update. A rejected helper means the requested character-mode policy is not enforced.

## Authoring and testing

Choose **Characters → Character identity & modes** on the character definition. Additional suits inherit its setting. Rebuild and reinstall the complete mod, then restart the game. Both uses a single character identity in both modes.

Check roster inclusion/exclusion, first-hover previews, selection, switching away and hub transitions. Story-specific requirements, cutscenes and every co-op combination are not guaranteed. Keep save backups; the helper does not directly grant persistent unlocks or change the saved game-mode flag, but this is not a guarantee against indirect gameplay effects.

## Optional crossover mods

Joker & Harley in Normal Mode and Heroes in Mayhem Mode use this same helper but are separate downloads. They are not required for Batcomputer character policies. Their F9/F10 toggles affect only those crossover overrides, not authored custom-character policies.

## Recovery

Close the game and move `LOTDKModeAccess` outside `ue4ss/Mods` to disable this helper before launch. If maintaining mods.txt, disable its entry too. Setting mods.txt to 0 alone is insufficient while enabled.txt remains. This also disables dependent character policies and crossover features. Do not delete saves or LOTDKExpanded as part of this procedure.
