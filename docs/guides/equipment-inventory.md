# Equipment inventory — September 5, 2026

The September 5 inspection found **52 native equipment lookup entries**, compared with 35 entries in the catalogue at that time. The workshop discovers all 52; uncataloged variants are inspection-only. Counts are from this installed extraction, not a promise that every user owns the same content.

The equipment trace covers 130 playable menu variants and 15 equipment types, including both tether launchers and the cross-folder Ninja Teleport definition. Drone and Remote Kitten use deployable-equipment registration with a controlled-pawn dependency. Native equipment clones have passed gameplay checks. Gordon's skeletal pistol replacement has also passed; other skeletal models are available as experimental imports on their original rigs.

51 actor/data graphs could be read. FreezeGun was blocked by the unextracted story-specific `BP_Hacking_QuickHack_MrFreeze` dependency. The inspector follows equipment definitions, instances, actor/projectile references and icon materials; it intentionally does not recursively clone ability, animation, Niagara or audio graphs. Zero below means no explicit static binding found in that traced graph, not that a gadget has no visuals anywhere.

These are **binding counts**, not unique models. Repeated mesh references and upgraded variants count separately. Skeletal fields can reference the same body twice. No NPC/boss entry gains a player adapter merely by being listed.

| Lookup suffix | Static bindings | Skeletal bindings |
| --- | ---: | ---: |
| Baseball | 2 | 0 |
| BaseballBat | 1 | 0 |
| Batarang | 5 | 0 |
| Batclaw | 3 | 2 |
| BigShield | 1 | 0 |
| Birdarang | 2 | 0 |
| Blowpipe | 6 | 6 |
| Hammer | 1 | 0 |
| Debris | 2 | 0 |
| Drone | 0 | 0 |
| DuckTank_FlameThrower | 1 | 0 |
| DuckTank_MachineGun | 1 | 0 |
| DuckTank_Rocket | 3 | 0 |
| Electrorang | 2 | 0 |
| EMPGrenade | 1 | 0 |
| FoamGun | 2 | 2 |
| FreezeGun | blocked | blocked |
| FreezeGunMech | 1 | 0 |
| Hackarang | 2 | 0 |
| CondimentMachinegun | 0 | 0 |
| MachineGun | 2 | 0 |
| DualMachinegun | 2 | 0 |
| EmptyMouthMachinegun | 1 | 0 |
| MarksmanRifle | 2 | 0 |
| NinjaGoonShurikenThrow | 2 | 0 |
| NinjaStar | 2 | 0 |
| NinjaTeleport | 1 | 0 |
| UmbrellaClosed | 2 | 0 |
| Umbrella | 1 | 2 |
| UmbrellaRocketLauncher | 1 | 0 |
| Pistol | 2 | 0 |
| PlasmaBeam | 3 | 0 |
| PlasmaCannon | 2 | 0 |
| Talia_PlasmaEMP | 4 | 0 |
| RasThrowingStars | 2 | 0 |
| RasThrowingStarsRapid | 2 | 0 |
| RasThrowingStars_P2 | 2 | 0 |
| RemoteControlledKitten | 2 | 0 |
| RocketLauncher | 3 | 0 |
| RocketLauncher_RHO | 3 | 0 |
| RubberBulletGun | 4 | 2 |
| Shield | 1 | 0 |
| Shotgun | 2 | 0 |
| SmokeBomb | 3 | 0 |
| SnapDragon_AcidHead | 0 | 0 |
| StunBaton | 1 | 0 |
| Katana | 1 | 0 |
| Katana_TaliaBoss | 1 | 0 |
| TaliaThrowingRangRapid | 2 | 0 |
| ElectricTetherLauncher | 1 | 2 |
| TetherLauncher | 1 | 2 |
| Whip | 0 | 0 |

## Projectile findings

Batarang has separate held, ordinary, alarm, concussive and bat-swarm model bindings. The Rubber Bullet Gun exposes its loaded bullet plus normal, focus and scoped projectile models. Its body is `Sk_GA_RubberBulletPistol_Gordon`, a skeletal mesh.

Rubber projectile data also contains native speed/acceleration and ricochet settings, and the instance carries a native range. Those are candidates for a later per-family tuning adapter, not controls exposed or gameplay-verified by this workshop pass. Beam, tether, remote-control and ability-driven gear need separate handling; a model replacement alone cannot implement their behavior.

The workshop warns about untested skeletal equipment rather than locking the entire donor. Imports still need valid native rigs and weights. A listed NPC or boss weapon is not a promise that its abilities work on a playable character; check the support banner and test the result.
