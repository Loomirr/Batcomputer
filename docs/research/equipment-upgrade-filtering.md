# Selective equipment upgrades — research only

## Observed native chain

Read-only copies from the active game extraction establish:

- Batman's `DA_DCMD_Batman_TheBatman2025_Playable.UpgradeDataAssets` references both Batarang and Batclaw functionality data assets.
- `/Game/Characters/Equipment/Batarang/Upgrades/DA_UF_BatarangUpgrades.UpgradeSets` contains eight entries: Number of Batarangs, Concussive, Stealthy Stun, Speedy Stun, Scatterangs, Alarmarang, Extra Damage and Bat Swarm.
- `DA_Batarang_NumberofBatarangsUpgrade.UpgradeData` references the menu/progression asset `DA_GadgetUpgrade_NumberOfBatarangs`.
- Its `UpgradeFunctionality` references `BP_UF_NumberOfBatararngsEffect`, which applies `GE_NumberOfBatarangsUpgrade`. The effect modifies the `HasNumberOfBatarangsUpgrade` attribute.
- The native Batarang instance also references the special projectile variants and their separate HUD materials/textures.

These observations describe serialized assets, not a completed runtime test of filtering.

## Proposed first proof

Keep the native upgrade ownership/purchase data unchanged. Clone the Batarang functionality data asset under the suit/mod namespace, retain only the Number of Batarangs entry, and repoint only that suit's Batarang entry in `UpgradeDataAssets`. Preserve the Batclaw entry and all unrelated character data. Do not remove global unlock tags or patch the shipped functionality set.

This is a plausible suit-local whitelist, not yet a guarantee of per-equipment isolation. Functionality effects apply through character attributes; two Batarang-derived items on the same suit may share those attributes. Trace selection/unselection and ability-condition evaluation before promising independent upgrade policies for multiple items on one character.

Test with upgrades already purchased as well as a clean test save. Confirm multi-throw works, disabled special modes cannot be selected or triggered, native Batarangs on other suits retain every upgrade, co-op players remain independent, and save/restart does not restore filtered effects. Review upgrade-menu presentation separately from runtime functionality so it does not advertise unsupported modes for the custom item.

No upgrade filters, progression changes or runtime DLL changes were implemented during this research. The first proof should remain isolated and assets/config-only unless evidence establishes a need for additional runtime support.
