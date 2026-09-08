# How custom-character registration works

These notes cover the first registration test on 6 September 2026. The new character, both suit variants, and their abilities worked in-game. The editor now builds the same assets and configuration without changing the runtime DLLs.

For the normal workflow, see [Custom characters](../guides/custom-characters.md).

## More than a character group

A group tells the game who owns the suits, but it isn't enough on its own. The working setup includes:

| Asset or setting | What it does |
|---|---|
| Character group | Holds the owner tag, display name, default suit, emblem and vehicle references |
| Pawn metadata (DCMD) | Links each suit's tag to playable, menu, cutscene and UI assets |
| UI metadata (UIMD) | Supplies the character and suit portraits |
| Progress definitions | Give each variant an unlocked, saveable state |
| Gameplay-tag config | Registers the character and suit tags |
| Asset Registry rows and bundles | Make the new assets discoverable and load their dependencies |
| Additive roster config | Adds the character to the selector without replacing its existing list |

The native group has no explicit suit array. The pawn metadata system supports child-tag lookups, and the test's two child suits appeared under their new owner.

## Example identities

These names are examples, not files shipped by Batcomputer.

- Character: `Pawns.Playable.CustomCharacter`
- Default suit: `Pawns.Playable.CustomCharacter.CustomCharacter`
- Extra suit: `Pawns.Playable.CustomCharacter.NoHood`
- Group: `DA_CharacterGroup_CustomCharacter`
- Progress: `GameProgress.Definitions.Characters.CustomCharacter.CustomCharacter`

Character and variant IDs need to be unique across installed mods. A different mod name doesn't prevent duplicate pawn tags.

The gameplay donor stays separate from the character owner. It supplies movement, combat, input and rig support while the new group owns the roster entry.

## Native details worth keeping

Batman and Catwoman groups contain `BaseCharacterTag`, `DisplayName`, `Symbol`, `DefaultCharacterVariant`, `DefaultVehicle`, `bAllowPlayerVariantSelection`, and `AbilityTags`.

`DinnerCharacterSelectSystem` uses `CharacterSelectSystem` configuration and its `SortedCharacterTags` list. The working test uses additive plugin configuration.

`PROG_Characters` contains the native saveable character definitions. A new character gets a separate progress-definition asset rather than replacing it. Alfred's native entry includes `GameProgress.Type.ExcludeFromCharacterMenu`; that flag isn't suitable for a new free-roam character.

The registry needs primary-asset IDs, scan coverage, and loading bundles. Those do different jobs. An asset being present in a registry doesn't by itself prove the game has loaded it.

## Test results and limits

Offline checks read back all 27 test packages, decoded 10 textures and the custom head attachment, and verified the two variants' metadata links and initially unlocked progress. The registry writer verified four primary rows and six loading bundles. The original saved suit stayed unchanged.

In-game testing confirmed roster discovery, both variants, and their abilities. Scripted story appearances weren't supported. New dialogue, mission permissions, and a full co-op/restart test run are still separate checks.

The frontend remains Batman-only. Adding a playable roster entry doesn't change that behavior.

## Sources

The investigation used native group assets, `PROG_Characters`, `PROGR_Characters`, character metadata, exported game configuration, and generated headers for the group, pawn, character-select and progression systems. Some config exports were pre-existing; they weren't all freshly re-extracted during the test.

The relevant Batcomputer services include `PawnTagConfigService`, `RegistryPluginService`, `CharacterRegistryBundleService`, and the mod builder.

See Epic's documentation on [Gameplay Tags](https://dev.epicgames.com/documentation/unreal-engine/using-gameplay-tags-in-unreal-engine?lang=en-US) and [Asset Management](https://dev.epicgames.com/documentation/en-us/unreal-engine/asset-management-in-unreal-engine) for the underlying Unreal systems.
