# Joker & Harley in Normal Mode

Play as registered Joker and Harley Quinn suits in the normal game's character menu, with full-color previews. Their native Mayhem roster is unchanged. Requires compatible LOTDK UE4SS, owned/installed Mayhem DLC and Steam executable profile 1344350.

Close the game, back up saves, and extract **Binaries** into **LEGOBatmanLotDK**. The included LOTDKModeAccess native helper is required. It is identical to the helper bundled with Heroes in Mayhem; both mods can be installed together. Do not replace your existing mods.txt.

**F9** toggles this mod for the current session; confirmation appears in the UE4SS console/log. Switch back to normal heroes and return to title before disabling, then reload. Existing characters/menus are not forcibly reset. For a persistent default, edit `ENABLED_ON_STARTUP` in this mod's `Scripts/main.lua` while the game is closed.

For full instructions, recovery steps and known limitations, read **Binaries/Win64/ue4ss/Mods/LOTDKModeAccess/README.md**. To fully disable before launch, move this mod folder outside Mods. A mods.txt value of 0 alone is not enough while enabled.txt remains present.

Cross-mode gameplay was confirmed working with this exact helper build. An intermittent helper startup failure remains under investigation: if the log says DISABLED, toggling cannot reactivate it for that session. Close the game and restart; if it repeats, include UE4SS.log in the report. This package preserves the tested binary, including failure diagnostics. No DLC entitlement bypass, game assets or saves are included.
