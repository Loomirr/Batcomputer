# LOTDK crossover mods

Two independent mods for LEGO Batman: Legacy of the Dark Knight:

- **Joker & Harley in Normal Mode** adds their registered playable suits to the normal character menu.
- **Heroes in Mayhem Mode** adds the base-game playable character families to Mayhem, including a fix for switching away from Batman.

Both include the same required native **LOTDKModeAccess** helper. These are Lua-controlled native mods, not standalone Lua scripts. Install either or both; the identical helper files can be merged.

## Requirements and installation

Requires a compatible LOTDK UE4SS installation and owned/installed Mayhem DLC. Supports the Steam game executable profile **1344350** (UE 5.6.1). The helper checks the executable and its patch sites; unknown/changed builds are disabled rather than patched. This does not unlock DLC ownership or provide missing assets.

1. Close the game and back up your SaveGames folder.
2. Extract the ZIP's **Binaries** folder into your game's **LEGOBatmanLotDK** folder. The helper should end up at `Binaries/Win64/ue4ss/Mods/LOTDKModeAccess/dlls/main.dll`.
3. If upgrading, replace the included helper and selected Lua mod files. Both installed crossover mods must use the same current helper version. Keep your backups outside the Mods folder.
4. The included enabled.txt files enable the mods. If you maintain mods.txt, put `LOTDKModeAccess : 1` before the installed crossover mod entries (`LOTDKJokerHarleyNormal : 1` and/or `LOTDKHeroesMayhem : 1`). Do not replace your entire mods.txt.
5. Restart the game. FreePartner is not required.

## Toggle keys

| Shortcut | Mod |
| --- | --- |
| F9 | Joker/Harley in normal mode |
| F10 | Heroes in Mayhem |

The game must have keyboard focus. Each key toggles only its own mod for the current session and prints one confirmation in the UE4SS console/log. It does not add a button to the game's menu. A conflicting registered shortcut is reported and not overwritten.

**Before disabling:** switch back to the mode's native characters (Joker/Harley in Mayhem; normal heroes in normal mode), then return to the title screen. Toggle and reload your save to refresh the roster. Already-open menus, spawned characters and cached rosters are not forcibly rebuilt or removed. If anything remains stale, quit and use the startup setting below. Toggling is not an emergency DLL unload or save repair.

Toggles reset on restart. For a persistent default, edit `ENABLED_ON_STARTUP = true` to `false` at the top of the relevant `Scripts/main.lua` while the game is closed. You can then enable it with its shortcut. Authored Batcomputer character-mode policies are separate and are not disabled by these shortcuts.

## Disable before launch / recovery

To fully disable one mod, close the game and move its entire `LOTDKJokerHarleyNormal` or `LOTDKHeroesMayhem` folder outside `ue4ss/Mods`. Alternatively set its mods.txt entry to 0 **and** rename/remove its enabled.txt: enabled.txt can still start a mod marked 0 in mods.txt.

To isolate the whole crossover helper after a startup problem, also move **LOTDKModeAccess** outside Mods and disable its mods.txt entry. This also disables any Batcomputer mode policies depending on that helper. Do not remove LOTDKExpanded or overwrite/delete saves as part of this procedure. Keep the shared helper installed when disabling only one crossover mod and continuing to use the other.

## Logging and troubleshooting

Normal output is one helper startup line, one startup line per installed crossover mod, toggle confirmations, and genuine warnings/errors. Hover/roster/preview/party diagnostics are quiet by default. UE4SS and other mods can still produce their own logs.

For a bug report, set `DEBUG_LOGGING = true` in either crossover script, restart, reproduce the problem and include UE4SS.log. Restore false afterwards. Diagnostics are bounded to avoid flooding. If a key does nothing, check the log for a conflict, missing helper or unsupported executable. You can change `Key.F9` or `Key.F10` in the corresponding script to resolve a conflict.

## Testing and known limitations

The author confirmed cross-mode gameplay works with the exact helper shipped here, following earlier successful preview and Mayhem Batman-switching tests. The F9/F10 callbacks were observed in the game log; their state handling also has automated mock coverage. Story-specific character requirements, abilities, vehicles, missions and all co-op combinations are not comprehensively validated. Use free roam and keep backups.

**Known issue: intermittent startup failure.** One prior launch failed during call-site setup and the helper rolled back its changes. This build adds exact failure diagnostics; the cause is not yet confirmed or fixed. A later launch and gameplay test succeeded. If the log says `DISABLED` or the scripts say `helper not active yet`, F9/F10 cannot repair that session. Close the game and restart. If it happens again, keep UE4SS.log and report the `Setup failure` line; do not change or delete saves to troubleshoot it.

The helper retains its internal diagnostic identifier for troubleshooting. These ZIPs preserve the exact tested binary, and routine gameplay logging remains quiet.

The helper does not directly write save unlocks, change the mode flag, grant entitlements or clear stored party preferences. That does not guarantee against indirect gameplay/save side effects. Future game updates may require a new helper. No custom-character test PAKs, saves, UE4SS installation or LOTDK Expanded runtime are bundled.
