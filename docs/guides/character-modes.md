# Character game modes

Custom character definitions can choose **Normal**, **Mayhem**, or **Both** in **Characters → Character identity & modes**. Choose a native playable gameplay donor separately from the character's appearance; a villain's cutscene model is not itself a safe playable donor.

1. Create/open the character definition and choose its game modes.
2. Customize its base, appearance and equipment. Joker and Harley gameplay donors require their installed DLC assets.
3. Add the character to a mod and build it. Additional suits inherit the saved character definition's mode setting.
4. Install/export the complete release, not just the PAK trio. The per-plugin policy is read by the updated LOTDKExpanded DLL. No separate Mode Access DLL is packaged.

Existing projects default to Normal. Changing this setting keeps the character ID and pawn tags unchanged. Both uses one character identity across the two modes, not two separate characters.

## Experimental limitations

The revised crossover helper has passed reported in-game checks for previews in both modes, Mayhem hub entry and switching away from Batman. The user's normal saves also returned to their expected state. Those results do not establish the cause of the earlier save-summary issue or guarantee every authored character is safe. Back up saves and test each custom character separately.

Integrated character modes support Steam executable profile 1344350 and reject unknown builds. Mayhem requires the DLC. Batcomputer checks the installed LOTDKExpanded capability receipt and DLL hash before building Mayhem/Both characters. Roster access does not guarantee compatibility with every opposite-mode mission, cinematic, gadget or progression rule. Test with backed-up saves and restart after changing the policy.

The optional `LOTDKJokerHarleyNormal` and `LOTDKHeroesMayhem` downloads are Lua-only controls for the integrated runtime. They expose registered native characters in the opposite mode and leave custom characters' explicit policies alone. They are not required for authored character modes.

## Check a new character

- **Normal:** appears in the normal roster, not Mayhem.
- **Mayhem:** appears in Mayhem, not the normal roster.
- **Both:** the same character and its suits appear in both rosters.

Check first-hover previews, selection, changing away again, and hub entry. Additional suits inherit the character definition's setting. Change the definition, rebuild the whole mod, install its registry plugin, then restart with updated LOTDKExpanded; editing a child suit or copying only its PAK does not update availability.

When upgrading, move the old `ue4ss/Mods/LOTDKModeAccess` folder outside Mods and disable its mods.txt entry if present. Do not remove LOTDKExpanded. The integrated feature refuses to patch while the legacy DLL remains installed. Old configuration files remain compatible, but rebuild old releases to stop distributing the helper.

If integrated character modes reject the game build or fail to initialize, native roster rules remain; requested modes are not guaranteed. Check `[LOTDKExpanded:CharacterModes]` in `UE4SS.log`. The integrated DLL has local test coverage and needs a fresh in-game acceptance run before uploading.
