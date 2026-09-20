# LOTDK Mode Access — experimental

This separate UE4SS native mod supplies Batcomputer's per-character Normal / Mayhem / Both roster policies and two optional Lua switches. It does not replace LOTDK Expanded or change the global game-mode/save flag.

## Requirements

- The current compatible LOTDK UE4SS installation; the helper is built against the same local UE4SS ABI.
- Steam game executable profile **1344350** (PE timestamp `6AA08464`, image size `1B11B000`). Every patched function and call site is also byte-checked. A different executable is rejected without patching.
- Owned, installed Mayhem DLC for Mayhem and the playable Joker/Harley assets. This does not grant DLC or supply game assets.

This is an **in-game experiment**, not a proven public release. Back up your saves before testing. Availability is only a roster change: opposite-mode mission scripts, gadgets, vehicles, story/cinematic roles and progression have not been verified.

**Testing hold (September 19):** Version 0.2 plus the custom test pack produced locked previews, a blocked Mayhem hub transition and incorrect save summaries. The user reports both the hub and save recovered after isolation. That implicates the experimental combination, not a conclusively identified component. Version 0.3 is a revised test candidate, not a verified public release; test it without BCModeTests. Preserve saves before testing. Having no direct save-write hooks does not prove gameplay/save safety.

## Installation

Quit the game. Place this helper at `LEGOBatmanLotDK/Binaries/Win64/ue4ss/Mods/LOTDKModeAccess/dlls/main.dll` with a nonempty `enabled.txt` in `LOTDKModeAccess`.

Each optional Lua mod belongs in its own sibling Mods folder:

- `LOTDKJokerHarleyNormal`: adds registered Joker/Harley variants to the normal roster; does not change their native Mayhem roster.
- `LOTDKHeroesMayhem`: adds registered base-game playable families to Mayhem; does not change the normal roster. This excludes NPC-only actors and vehicles. Custom characters retain their explicitly authored policy.

Both scripts require this helper; they are not standalone pure-Lua roster implementations. Both can be enabled together. The two ZIPs contain the same helper, so install it only once. Do not overwrite a different/newer helper without keeping a backup and checking compatibility. No `mods.txt` replacement is supplied: if yours explicitly disables one of these named mods, enable that entry too.

Restart the game after changing mods/policies. Do not hot-reload/unload the native helper while running. The roster may already be cached.

To remove an optional Lua mod, quit the game and remove only its own Mods folder. Retain the shared helper while any installed Batcomputer character uses Mayhem/Both. Removing the helper alone does **not** hide those custom characters: their normal native fallback may show them in normal mode instead.

## Policies and precedence

Batcomputer writes each registry plugin's `Config/BatcomputerCharacterModes.ini`:

```ini
[Batcomputer.CharacterModes.v1]
Pawns.Playable.MyCharacter=both
```

The helper loads policies on Unreal initialization from `ue4ss/LOTDKExpanded/RegistryPlugins/<Plugin>/Config/`. All suits under the character family inherit the definition's policy. Case-insensitive conflicts retain native behavior and log an error. Invalid files are ignored. Authored policies take priority over the optional Lua switches.

## Implementation boundary

The native roster compares `PawnTag.MatchesAny(VillainCharacterGroups)` with its existing mode boolean. Version 0.3 replaces exactly two verified five-byte CALL instructions inside that roster builder, using register-preserving jump relays. It does not detour the roster entry point, the global tag-matching function, the global availability function or any completion function. The match relay passes the native R15B mode value directly; it never queries, forces or changes mode progression. Both keeps one identity instead of producing duplicate metadata/suit tags.

The optional crossover overrides only the roster's availability CALL for an included opposite-mode native character. A one-shot, same-caller-stack-slot guard prevents a different caller inheriting the override. No saved unlock value is assigned. Registered costume variants may appear; this is not an entitlement or asset loader, and other gameplay systems can enforce their own checks.

The preview candidate adapts the older `SetPreviewLocked(false)` UI approach: only the exact native character-screen Blueprint event, a reflected GameplayTag from the active menu, and the screen's `IsVM` mode field are accepted. Only an explicitly enabled opposite-mode native family is affected. Native same-mode locks, custom character policies, vehicle previews and story/other-player prohibition flags are not overridden. This must still be visually checked in-game.

### 0.4 follow-up

The 0.3 game test passed the normal-save and Mayhem access checks, but previews remained locked and Batman could become stuck in party slot 1. The 0.3 preview lookup was wrong: `GetSelectedOption` is a **SuitMenu** function, not a CharacterSelectScreen function. The corrected path uses the active suit submenu or main menu's cached selected button. Bounded diagnostics report unresolved tags instead of claiming success.

0.4 also clears a cross-mode suit button's UI lock after its native lock event, using that exact button's tag and owning screen's mode. This makes all registered crossover-family suits eligible in the menu without assigning saved progression values; no entitlement/asset loader is added. Native same-mode locks and prohibition flags remain untouched.

A third, byte-guarded call-site patch handles `ToggleLockedCharacterGroup=Batman` in native party-toggle selection. It returns no Batman-specific lock only when HeroesMayhem is enabled and the same character-selection system's latest native roster observation was Mayhem. Other party/mode/story checks are not bypassed. This candidate still needs a full-menu slot-1 switch-away test and preview verification. Hold-Tab quick swap is not available in native Mayhem and is not added by these mods.

### 0.5 preview materials

The 0.4 test removed suit locks, but models stayed silhouetted until selected and the menu reopened. The native preview query exempts active party members, explaining that behavior. Its result is copied into the pocket actor's asynchronous model-load request BEFORE the widget's SetPreviewLocked event; changing that later event cannot prevent the locked material being applied.

0.5 adds one verified call-site replacement at the character-preview dispatch's lock query. It checks the exact character screen, reflected IsVM, and the requested metadata's reflected PawnTag (including native offset/type/size). Only enabled opposite-mode native families return unlocked. The native load request now receives that result before loading the model; no material restoration, global unlock hook, delayed polling or current-party modification is used. Vehicle previews and same-mode locks are not overridden. The earlier Mayhem Batman slot fix still needs gameplay verification.

### 0.6 party preference pin

The user confirmed 0.5 previews work in both modes, but Batman still could not leave Mayhem slot 1. The old FreePartner mod identified a separate `BatmanGroupTag` preference pin. 0.6 redirects only the native pin predicate's tag comparison: it does not clear that shared preference, install FreePartner, or poll/mutate party objects. The exact live prefs Blueprint, reflected field layout, native caller, Batman family and enabled HeroesMayhem are required. A fresh read-only native mode query through the party's registered save-system context must confirm Mayhem; unknown mode retains the native result. Normal-mode behavior and other uses of the preference are left unchanged. This new party fix needs gameplay verification.

Unknown profiles, invalid policies and hook setup failures fall back to original behavior. Read `UE4SS.log` for `[LOTDKModeAccess]`, the `0.6 crossover test active` startup line, `Native preview material unlocked` and `Mayhem party preference pin bypassed` lines. No success claim should be based only on the Lua startup line. This remains a private in-game candidate, not a validated release.

## Building

### 0.7.1 shortcut revision

Uses unmodified F9 for Joker/Harley in normal mode and F10 for heroes in Mayhem. No gameplay hook changes; updated Lua tests exercise the no-modifier registration overload without ModifierKey present.

### 0.7 release packaging

The user confirmed 0.6 previews and Mayhem Batman switching work. 0.7 preserves those hooks, makes bounded routine traces opt-in per Lua switch (`DEBUG_LOGGING`), and adds independent session shortcuts Ctrl+F10 / Ctrl+F11. Startup enable defaults remain editable. Errors and toggle confirmations remain visible. Shortcuts change override flags, not the installed hooks, live actors, cached menus or saves; title/reload and offline disable instructions are in UserGuide.md. No FreePartner source is redistributed. See the upload ZIPs' user guide rather than this internal research history.

`Build.ps1` reuses the local parent project's UE4SS shipping ABI flags and libraries. It builds only this helper and runs pure policy and Lua-mock checks. It does not configure or install the main LOTDK Expanded runtime. The resulting DLL is copied to Batcomputer's `Data/ModeAccess/main.dll` for packaging.

Local checks do not replace gameplay tests of hover/spawn, both mode transitions, save/reload, co-op or cross-mode equipment.
