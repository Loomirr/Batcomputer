# Custom equipment: registration and testing

The banana Batarang metadata fix is **confirmed working in-game**: the custom equipment appears, can be used, and inherits working Batarang upgrades. Some native Batarang effects remain. The first [Equipment workshop](equipment-workshop.md) now brings independent static-model and visual bindings into the tool; that generalized authoring flow still needs in-game acceptance testing. Independent held props and fighting styles remain separate systems.

## September 5 metadata-loading correction

The first V2 retest still left the banana slot empty. Native equipment initialization reads the character DCMD's soft `EquipmentList` entries; merely registering the custom ETA and its definition did not reproduce the character's loading contract. The captured V2 registry had no character bundles at all.

Batcomputer now reads the **finished staged DCMD** when building a mod and writes:

- `ASSETBUNDLE_METADATA`: its actual equipment lookup entries and non-null upgrade references, including native and DLC dependencies.
- `ASSETBUNDLE_GAMEPLAY`: its playable actor class; each referenced custom ETA also gets its own equipment-definition gameplay bundle.
- `ASSETBUNDLE_CINEMATIC`: its cinematic actor class, when present.

The writer reloads the cooked registry and checks every bundle name and asset path. Unreadable metadata or missing staged mod-owned dependencies stop the build. Native dependencies are referenced, not overwritten or added as mod-owned registry rows.

The metadata-fix test changed only the registry; the suit's trio, loose tags, manifest, and descriptor stayed the same. Neither runtime DLL changed. In-game testing then confirmed the equipment slot, attacks, and inherited Batarang upgrades worked.

## Earlier registration retest

Native Batarang and Batclaw registry entries include an `ASSETBUNDLE_GAMEPLAY` reference to their equipment definition class. The old banana registry entry omitted this loading metadata. The writer now preserves explicitly requested gameplay bundles and verifies them after saving; an older writer cannot silently pass without them.

The new `Banana Equipment V2` test also uses a unique primary-asset name and places its mod-owned ETA lookup below `/Game/Characters/Equipment/Mods/BananaEquipmentV2/`, inside the existing equipment discovery tree. Its definition, instance, held model, projectile models and icon remain below `/Game/Mods/BananaEquipmentV2/`. No shipped Batarang package is replaced, and no runtime DLL or shared scan configuration is changed.

Offline checks verify all 18 cooked packages, the matching ETA/definition gameplay tags, the suit's metadata and runtime equipment slot, the localized menu name, and the registry gameplay bundle. These establish correct package structure, not in-game success.

## What to test in-game

On Batman, select **Banana Equipment V2** after a full game restart. Check the banana HUD icon, switching between it and the Batclaw, aiming, quick throwing, the held/projectile models, and returning to an ordinary Batman suit with its original Batarang. Then reselect the test after a restart.

The test retains native Batarang abilities, animation rules and upgrade data while isolating equipment registration/loading. Fully independent progression remains follow-up work. The accepted banana proof does not establish that every NPC or boss equipment family has usable player controls.

Experimental held-item effects/status tests remain parked and are not included.
