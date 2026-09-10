# Selective equipment upgrades — experimental adapter

## Observed native chain

Read-only copies from the active game extraction establish:

- Batman's `DA_DCMD_Batman_TheBatman2025_Playable.UpgradeDataAssets` references both Batarang and Batclaw functionality data assets.
- `/Game/Characters/Equipment/Batarang/Upgrades/DA_UF_BatarangUpgrades.UpgradeSets` contains eight entries: Number of Batarangs, Concussive, Stealthy Stun, Speedy Stun, Scatterangs, Alarmarang, Extra Damage and Bat Swarm.
- `DA_Batarang_NumberofBatarangsUpgrade.UpgradeData` references the menu/progression asset `DA_GadgetUpgrade_NumberOfBatarangs`.
- Its `UpgradeFunctionality` references `BP_UF_NumberOfBatararngsEffect`, which applies `GE_NumberOfBatarangsUpgrade`. The effect modifies the `HasNumberOfBatarangsUpgrade` attribute.
- The native Batarang instance also references the special projectile variants and their separate HUD materials/textures.
- Functionality roots and sets are primary assets. Roots load their sets through `ASSETBUNDLE_METADATA`; sets load functionality classes through `ASSETBUNDLE_GAMEPLAY` and purchase metadata through `ASSETBUNDLE_METADATA`.

These observations describe serialized assets, not a completed runtime test of filtering.

## Implemented first adapter

The workshop saves excluded upgrade IDs for Batarang-based custom equipment. Packaging clones the Batarang functionality root under the item's mod namespace, retains the selected entries and repoints only its corresponding `UpgradeDataAssets` slot. An empty selection emits an empty functionality root; old recipes select all eight. Native purchase/progression references and unrelated equipment remain unchanged.

Alarmarang and Concussive each include a `Batarang_UpgradeFunctionality` whose `EquipmentToModify` points to `BP_Batarang_Instance_C`. The adapter clones the selected functionality sets and class-targeted functions, then binds them to the custom instance. Combo and Scatterang apply attribute effects without that class target; their success alone does not establish special-mode compatibility.

The effects use shared character attributes. Filtered builds with more than one Batarang-family item in the final equipment list are therefore blocked. Other donor types do not expose filtering yet. Unknown/duplicate IDs, changed native upgrade lists and mismatched class targets fail closed. Cancelling the upgrade editor does not mutate the saved recipe.

After primary-asset registration and native loading bundles were restored, all-upgrades and no-upgrades configurations passed in-game. Each equipment owner gets distinct primary-asset names under `/Game/Characters/Equipment/Mods/`.

The remaining “multi-only” report exposed a naming mistake in the test: `DA_GadgetUpgrade_NumberOfBatarangs` is **Batarang Combo**, described by the native UI as increased capacity for quick successive throws. **Scatterang** (`DA_GadgetUpgrade_Scatterang`) grants simultaneous throws and three-target lock-on. The old test enabled Combo, not Scatterang. Labels now distinguish them; saved IDs retain their old mapping so existing choices are not silently changed.

Test with upgrades already purchased as well as a clean test save. Confirm multi-throw works, disabled special modes cannot be selected or triggered, native Batarangs on other suits retain every upgrade, co-op players remain independent, and save/restart does not restore filtered effects. Review upgrade-menu presentation separately from runtime functionality so it does not advertise unsupported modes for the custom item.

This is an experimental implementation, not an in-game certification. The global upgrade menu and purchases are not edited. Bat Swarm is marked as focus-only in the native projectile data and needs a separate focus-throw check. No progression or runtime DLL patches are part of the adapter.
