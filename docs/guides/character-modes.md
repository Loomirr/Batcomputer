# Character game modes

Custom character definitions can choose **Normal**, **Mayhem**, or **Both** in **Characters → Character identity & modes**. Choose a native playable gameplay donor separately from the character's appearance; a villain's cutscene model is not itself a safe playable donor.

1. Create/open the character definition and choose its game modes.
2. Customize its base, appearance and equipment. Joker and Harley gameplay donors require their installed DLC assets.
3. Add the character to a mod and build it. Additional suits inherit the saved character definition's mode setting.
4. Install/export the complete release, not just the PAK trio. Mayhem/Both releases also contain the shared experimental `LOTDKModeAccess` UE4SS helper and a per-plugin policy.

Existing projects default to Normal. Changing this setting keeps the character ID and pawn tags unchanged. Both uses one character identity across the two modes, not two separate characters.

## Experimental limitations

The revised crossover helper has passed reported in-game checks for previews in both modes, Mayhem hub entry and switching away from Batman. The user's normal saves also returned to their expected state. Those results do not establish the cause of the earlier save-summary issue or guarantee every authored character is safe. Back up saves and test each custom character separately.

The current helper supports the verified Steam executable profile 1344350 only and refuses unknown builds. Mayhem requires the DLC. Roster access does not make a character compatible with every opposite-mode mission, cinematic, gadget or progression rule. Test with backed-up saves and restart after changing the policy.

The separate optional `LOTDKJokerHarleyNormal` and `LOTDKHeroesMayhem` Lua mods use this same helper to expose registered native characters in the opposite mode. They leave custom characters' explicit policies alone. They are not required just to use a custom character's Normal/Mayhem/Both setting.

## Check a new character

- **Normal:** appears in the normal roster, not Mayhem.
- **Mayhem:** appears in Mayhem, not the normal roster.
- **Both:** the same character and its suits appear in both rosters.

Check first-hover previews, selection, changing away again, and hub entry. Additional suits inherit the character definition's setting. Change the definition, rebuild the whole mod, install its registry plugin and runtime dependency, then restart the game; editing a child suit or copying only its PAK does not update availability.

If the helper is absent or rejects the game build, the game keeps its native roster rules; the requested modes are not guaranteed. Check `[LOTDKModeAccess]` in `UE4SS.log` and do not treat an inactive helper as a successful test.
