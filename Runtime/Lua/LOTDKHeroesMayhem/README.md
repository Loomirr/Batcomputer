# Heroes in Mayhem Mode

Use the base-game playable character families in Mayhem's character menu, with full-color suit previews and a fix for switching away from Batman. Includes Batman/Bruce Wayne, Gordon, Catwoman, Robin, Batgirl, Nightwing, Talia variants, Thomas Wayne, Alfred and Lucius Fox where their playable assets are installed. NPC-only characters and vehicles are not added.

Requires compatible LOTDK UE4SS, owned/installed Mayhem DLC and Steam executable profile 1344350. Close the game, back up saves, and extract **Binaries** into **LEGOBatmanLotDK**. The included LOTDKModeAccess native helper is required and can be shared with Joker & Harley in Normal Mode. Do not replace your existing mods.txt. FreePartner is not required.

**F10** toggles this mod for the current session; confirmation appears in the UE4SS console/log. Switch back to native Joker/Harley and return to title before disabling, then reload. Existing characters/menus are not forcibly reset. For a persistent default, edit `ENABLED_ON_STARTUP` in this mod's `Scripts/main.lua` while the game is closed.

Use the full character menu; this mod does not add hold-Tab quick swap to Mayhem. Normal-mode hero restrictions are unchanged.

Read **Binaries/Win64/ue4ss/Mods/LOTDKModeAccess/README.md** for recovery and known limitations. To fully disable before launch, move this mod folder outside Mods. A mods.txt value of 0 alone is not enough while enabled.txt remains present.

Cross-mode gameplay was confirmed working with this exact helper build. An intermittent helper startup failure remains under investigation: if the log says DISABLED, toggling cannot reactivate it for that session. Close the game and restart; if it repeats, include UE4SS.log in the report. This package preserves the tested binary, including failure diagnostics. No DLC entitlement bypass, game assets or saves are included.
