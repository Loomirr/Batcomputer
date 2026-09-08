# New characters with their own suits — research, 6 September 2026

These notes record the first custom-character registration test. The character, both suits, and their abilities later worked in-game, and the editor now uses that registration path without runtime DLL changes. See [Custom characters](../guides/custom-characters.md) for the current workflow.

## Initial assessment

This section records the questions raised before the first in-game test. The results and remaining test coverage are listed below.

Feasible, with much of the asset-authoring work already available. A first existing-rig, donor-gameplay character is a medium-to-high difficulty registration proof, not another skeletal import problem. Production-ready roster integration, progression, save/load and scripted story behavior remain the harder part. A character group alone is not sufficient.

The smallest useful proof is **one genuinely new character with two visibly different suits**, while retaining Batman as a separate, unchanged roster entry. The test started from an existing custom suit. It reuses its proven Gray Ghost gameplay donor, with no new animations, skeletons, equipment controllers or custom voice work. The second suit removes only the head attachment; both start unlocked.

## Confirmed structure and proposed mapping

The tags below are generic examples, not installed asset names. Each character needs unique owner and variant IDs; actor and metadata names must also avoid collisions with existing suits.

| Layer | Example | Purpose |
|---|---|---|
| Character identity | `Pawns.Playable.CustomCharacter` | Independent roster owner; not another Batman suit tag |
| Group asset | `DA_CharacterGroup_CustomCharacter` | Base tag, character name/emblem, default suit and permission to select variants |
| Default suit | `Pawns.Playable.CustomCharacter.CustomCharacter` | Own DCMD, playable, cutscene and UI metadata |
| Second suit | `Pawns.Playable.CustomCharacter.NoHood` | Second independent identity beneath the same group; head attachment removed |
| Progress | `GameProgress.Definitions.Characters.CustomCharacter.CustomCharacter` / `.NoHood` | Unlock, viewed and saved state for each suit |
| Gameplay donor | Retain the existing custom character suit's proven donor | Movement, combat, interaction and input machinery; separate from roster ownership |

The current base-game group assets were read directly from the installed containers. Batman's group contains `BaseCharacterTag`, `DisplayName`, `Symbol`, `DefaultCharacterVariant`, `DefaultVehicle`, `bAllowPlayerVariantSelection=true`, and `AbilityTags`. Catwoman has the same structure. **There is no explicit suit array on the group asset.**

The reflected `TtPawnMetaDataSystem` provides `GetPawnTagsWithin`, `GetPawnTagsWithinAny`, `GetAllPawnTags`, and `GetMetaDataForPawnTag`. Together with the native parent/child identities, this strongly supports discovering a group's suits through its child pawn tags. The exact menu-construction implementation is not present in the generated headers, so this still needs a new-group runtime test.

Unreal tags must be registered in the tag dictionary, not merely written as strings into assets. Our existing loose-tag packaging provides a starting point. [Epic: Gameplay Tags](https://dev.epicgames.com/documentation/unreal-engine/using-gameplay-tags-in-unreal-engine?lang=en-US).

## What else has to be supplied

1. **Discoverable primary assets.** The existing exported `DefaultGame.ini` scans `TtCharacterGroupDataAsset` / `/Script/TtCharacterGroupMetaData.TtCharacterGroupDataAsset` under `/Game/Characters/MetaData/Groups`. It separately scans `PawnMetaData` and `TtGameProgressDefinitionSet`. A group in an arbitrary folder is not automatically discoverable. Use a unique mod-owned subfolder inside the native group scan root, or prove an explicit additional scan rule. Extend the registry writer/allowlist narrowly for groups and progression rather than overwriting a shipped asset.
2. **Roster admission.** `DinnerCharacterSelectSystem` is `Config=CharacterSelectSystem`; its `SortedCharacterTags` contains the ordered character roots. The exported `DefaultCharacterSelectSystem.ini` confirms this list, including story characters. A new root must be admitted without replacing other mods' additions. Whether a standalone additive plugin config reaches this already-initialized subsystem is unproven.
3. **Real progress definitions.** The current native `PROG_Characters` is a `TtGameProgressDefinitionSet`, containing character enum definitions with `DefaultValue`, `InfoTags`, `bShouldSaveValue`, and `ProgressTag`. **Alfred explicitly carries `GameProgress.Type.ExcludeFromCharacterMenu`**, despite having a character group and appearing in the exported roster list. Do not copy that flag into a new free-roam character. The first proof should use its own definitions, initially unlocked, without new story requirements. `PROGR_Characters` demonstrates later story-conditioned unlock rules; these are not required merely to test an initially unlocked character.
4. **A complete suit identity for each variant.** Native DCMD links the exact pawn tag to `Pawn`, `MenuActor`, `CinematicsActor`, `UIMetaData`, `ProgressTag`, text, interactor data, equipment and upgrades. Reuse the tool's working playable/cutscene generation and gameplay/metadata/cinematic registry bundles. A menu preview working does not prove the cinematic class will load.
5. **Shared gameplay, separate ownership.** Batcomputer currently rejects a PawnTag whose character owner differs from its gameplay donor (`CharacterOwnerMismatchError`). This is correct for suits and should remain. A future explicit **New character** mode must instead validate the new group's ownership while retaining donor rig/ability/equipment compatibility. Simply disabling the existing check globally is unsafe.

Primary asset type/ID registration, scan rules and loading bundles are separate responsibilities in Unreal; a row in an Asset Registry is not proof that the game's character-group subsystem consumed it. [Epic: Asset Management](https://dev.epicgames.com/documentation/en-us/unreal-engine/asset-management-in-unreal-engine).

## Reusable code and remaining unknowns

Already reusable: native tags, DCMD/UIMD and text generation, independent suit packages, registry writer supporting multiple primary types, cinematic/loading bundles, existing-rig bodies, attachments, materials, abilities, combat styles, held items and custom equipment. `LOTDKExpanded` currently derives character scope generically from `Pawns.Playable.<owner>`, maintains selections per scope, and intentionally limits frontend startup preload to Batman. These are useful foundations, **not proof that it adds new roster groups**. Leave the Batman-only frontend policy intact.

Original research questions:

- Startup discovery of a custom group and a custom progress-definition set, including registry IDs and actual native group lookup.
- Safe additive roster configuration and menu materialization. Generated `TtCharacterGroupSystem` headers expose no registration function or map contents; their generated `.cpp` stubs are not the game's native implementation.
- Whether the two variants appear in the intended group without custom UI injection, and whether selections survive a full restart, character switching, co-op and cutscene loads.
- Story/mission filters that name existing characters explicitly. A new free-roam identity does not automatically earn donor-specific story roles, dialogue or mission permissions.

Try an assets/config-first proof. **Do not promise zero runtime work yet:** if the group registry or roster is initialized before the custom assets/config are admitted, a narrowly scoped registration extension may be necessary. Research did not change any DLL.

## Proof 01 and next in-game checks

Update, 6 September: the user confirmed that the independent character, both suit variants and their abilities worked fully in-game. Scripted story cutscene appearances were absent and accepted as unsupported. This does not establish new dialogue/story permissions or a complete co-op/restart matrix. The editor now uses the proven assets/config registration path without runtime DLL changes.

1. Copy the existing custom character visual recipe into an isolated proof; leave the original suit untouched. Register the new custom character group, two pawn tags and progress tags; package all assets in unique paths. Keep existing characters untouched.
2. Verify primary IDs for group, both DCMDs and progress set; verify exact DCMD lookup and child-tag enumeration before spawning.
3. Verify the new roster tile, correct name/emblem, two suit tiles and initially unlocked state.
4. Select each suit in gameplay; switch back to Batman; restart and retest both suits, cinematic loading and co-op. Start with a backup/test save.
5. Repeat these checks through the Characters tab as well as with the original test packages.

Offline proof 01 checks passed: 27 packages re-read from the final IoStore container, 10 texture payloads and the custom hood mesh decoded, both progress definitions initially Unlocked, exact playable/menu/cinematic links, original saved suit unchanged, and all non-head component properties equal between variants. The registry commandlet verified four exact primary rows and six character loading bundles. A native engine-source trace confirms plugin `Config/CharacterSelectSystem.ini` is the additive configuration layer; the subsequent in-game test confirmed roster discovery and both suits.

## Evidence and limits

- Fresh read-only container dumps: `artifacts/custom-character-group-assets.log` (Batman/Catwoman/Alfred); `artifacts/custom-character-progress-assets.log` (PROG_Characters, PROGR_Characters, Batman DCMD).
- Existing FModel exports: `<FModel export root>/LEGOBatmanLotDK/Config/DefaultGame.ini` and `DefaultCharacterSelectSystem.ini`. Direct re-extraction of these encrypted/compressed INI entries did not succeed in this pass; treat their exact current config values as requiring revalidation, not as a new live-runtime observation.
- Generated headers under the parent workspace's `UHTHeaderDump`: `TtCharacterGroupMetaData`, `TtPawnMetaData`, `DinnerCharacterSelect`, `DinnerCollectables`, `TtGameProgress`.
- Current code: `PawnTagConfigService.cs`, `RegistryPluginService.cs`, `CharacterRegistryBundleService.cs`, `MainForm.Mods.cs`, and the parent workspace's `NewSuitSlotNative/dllmain.cpp` (scope derivation, manifest validation, saved selection and Batman-only startup preload).
- July research is historical context only: the old donor ping-pong limitations are not assumed to describe the current native-manifest implementation.
