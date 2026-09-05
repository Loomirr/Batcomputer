# Suit abilities

Batcomputer can customize the gameplay abilities inherited from a suit's selected gameplay donor.
Open **Abilities**, then choose **Edit suit abilities**.

Every edit is suit-local. Batcomputer clones the required DPRD and AbilitySet assets into the mod;
it does not rewrite the donor, another suit, or the installed game.

## Edit the loadout

The **Current loadout** view shows the donor's AbilitySets in their exact authored order. The
**Ability-set library** includes readable base-game and installed-DLC sets from the active extract.

In the editor you can:

- Add, remove, restore, and reorder complete AbilitySets.
- Inspect the gameplay abilities granted by each readable set.
- Add a gameplay-ability package with its level and optional input tag.
- Remove or restore one inherited gameplay-ability grant.
- Use **Reset to donor** to discard the suit's complete custom loadout.

Saved ordering is significant and is reproduced in the generated DPRD. If the gameplay donor
changes later, Batcomputer checks the saved donor identity and AbilitySet fingerprint instead of
silently applying an old edit to a different loadout.

## Protected and required sets

Core sets may supply input, movement, health, spawning, combat, or save-state behavior. Their
destructive controls stay locked until **Advanced: allow removing or editing core ability
entries** is enabled and its warning is accepted. An arbitrary combination can still fail only
when its affected action runs, so test advanced changes on a duplicate suit first.

AbilitySets required by selected equipment or glider support are retained during packaging. Remove
or change that equipment/glider first if its controller set is no longer wanted; the ability editor
cannot create a broken dependency by deleting it from the visible loadout alone.

## Fighting styles and weapons

A character's fighting style or held weapon is usually not one isolated AbilitySet. It can depend
on gameplay abilities and effects, equipment definitions, spawned actors, animation montage/layer
sets, sockets, and presentation data. For example, borrowing Nightwing's sticks requires more than
adding one grant.

The **Fighting style** selector offers coordinated bundles for Batman, Catwoman, Nightwing,
Batgirl, Gordon, Talia (unarmed and prologue/ninja training), Lucius, Robin, Bruce's training combat, young-adult Bruce
(also used by Alfred/Thomas), child Bruce, and **Sword — player adapter (customizable)**. Applying one replaces the previous
melee style and its style-owned animation/support packages as one transaction; two combat styles
cannot be active together. Traversal and ordinary utility sets stay additive.

Story-limited styles intentionally retain their limitations. Young-adult Bruce suppresses focus,
critical hits, grabs and air attacks; child combat and Robin's moves use different body proportions.
Read the selected style's notes before applying it. Lucius/training/child sets have no combat-type
effect: applying them removes the outgoing combat-type effect rather than leaving conflicting tags.

The picker also discovers enemy/boss combat sources in the active AbilitySet library, including
the sword enemy. Entries marked **needs player adapter** are inspect-only, not working player
presets. Their models and animations are reusable, but AI input, targeting, attack sequencing and
equipment need a player-compatible implementation. Newly extracted sources appear when the editor
is reopened; unavailable sources are labelled rather than silently treated as compatible.

### Custom held items: current findings

Nightwing's sticks are LAM-managed actors, not an equipment-menu gadget: `GA_Item_Batons` spawns
two `BP_Baton_Robin` actors into `LAM.RightHand`/`LAM.LeftHand`, with animation-controlled drawing
and stowing. A custom version can clone the item ability and actor into the suit's mod, substitute
its own mesh/materials, and retain the authored hand/animation behavior. No global replacement is
needed. The sword preset now uses this managed-item route with one right-hand katana actor.

The sword enemy uses `BP_Katana_ED` → `BP_Katana_Weapon` → `SM_Katana`, with
`Equipment.Katana`/`Animation.Equipment.Blade` tags and right-hand/right-stow slots. Its weapon
actor includes hitboxes and effects; changing its visible mesh does not automatically adapt attack
timing or collision. The sword adapter uses player attack input and combo metadata with local
copies of the sword montages, not the entire goon AbilitySet or `InputData_Goon`.

An always-visible cosmetic item is simpler, but should still yield to hand slots during traversal,
interactions and cutscenes. A permanent hand attachment alone does not make it a damaging weapon.

Local acceptance status: Gordon, Robin, Lucius and Batgirl combat on Batman were reported working
in-game. The later native-style tests also passed user testing. The sword proof was reported
visible and able to attack without a target, with **1.5x** preferred over the earlier 2–3x speed.
That does not certify every donor/body pairing, custom asset, or visibility mode.

### Held items and sword combat

Held items are independent of fighting styles. Open **Abilities → Held items** to add, edit or
remove an item. Choose a native example, then select a hand,
model, material and visibility. You can use one independent item per hand. These entries appear
under **Held items [independent · staged]** in Current loadout even without a style override.

The library includes sword/katana, baseball bat, stun baton and closed umbrella melee actors,
plus cosmetic gel spray can, plant spray, Catwoman laser pointer, batarang, birdarang, ninja star,
smoke bomb, baseball, static Robin baton and Gray Ghost goggles.
Small cosmetic examples use a passive native actor with no collision; they do not import gadget
controllers, projectiles or enemy abilities. Their native models/materials are retained, including
the traced primitive-color data where required. Example details explain limitations in the editor.
Goggles are a hand prop, not headwear; the static Robin baton does not fold or supply a melee hitbox.
The fourteen examples and the held-item tool flow, custom models and left-hand items have passed user testing. This does not certify every new combination or transition.

Visibility options are **Always held**, **Only while attacking**, **During combat or attacks**,
and **Hide during combat / attacks**. The last option also hides during empty-space attacks.
Native hand-slot priority and `Status.BlockItemGA` are retained for competing gadgets/actions.
Always/outside-combat items use independently registered request tags, not the native baton
request or animation-context tags. New combinations and hide-during-combat still need in-game testing.

An item does not grant attacks, change the fighting style or add electrical stun/shockwave
abilities. For weapon attacks, select **Sword**, **Baseball bat** or **Baton — player adapter** separately and add a compatible
right-hand item visible during attacks. Saving/building blocks an adapter configuration without
that item. Other styles can carry decorative props, with a warning about native weapon conflicts.
Cosmetic examples do not satisfy the adapter's required melee hitbox, even if the model looks like a weapon.

The custom weapon editor is available from **Held items → Edit item → Open model editor**.
Import an OBJ, align it with numeric position/rotation/scale controls, toggle the original/custom
models, and assign a cooked material package per OBJ material slot. **Validate bake & use model**
checks the cooked mesh; save both parent editors and rebuild the suit to package it. Source geometry
and alignment are stored in the suit project. This first editor uses neutral/slot-color preview
shaders and mesh-local origin axes, not a calibrated hand-grip preview. Collision and hitboxes are
not resized with the model. See [weapon editor details](weapon-model-editor-plan.md).

1. Open **Abilities → Held items**, add the item, and choose **Use held items**.
2. Optionally choose **Sword**, **Baseball bat** or **Baton — player adapter**, apply its style, then open **Combat settings…**.
3. Save the Ability Explorer changes, then rebuild/package the suit normally.

New held items default to **always held**. Sword combat defaults to **1.5x** playback and **no
required combat target**. Combat settings controls speed (0.5x–3x) and target requirement.
Sword also supports four compatible LEGOfig attack montages. Bat uses two verified attack clips;
baton uses one deliberate slam. Their contact, hitbox and combo timing are adapted automatically,
so their source clips are shown read-only rather than accepting unverified raw enemy montages.
Held-item settings controls the cooked mesh, custom model
and optional material-slot-0 override. Blank material keeps the mesh's materials.
Package paths must resolve in the active extraction or this suit's staged content, not to raw
OBJ/PNG filenames. The normal OBJ/material tools remain separate; this is not an arbitrary
weapon rigging or hitbox editor.

All generated abilities, actor, mesh, attack montages and metadata are suit-local. Other
characters and the original game assets are not overwritten. Switching to a different style
removes the combat adapter but keeps independently configured items. **Reset to donor** removes
both ability and held-item edits. Old sword projects migrate their item, visibility, mesh,
material and custom-model recipe without dropping those settings. Config changes invalidate
the generated-asset cache and persist in the saved suit project.

Attack-only visibility requests the weapon while the player's melee ability is active and
releases it afterward; higher-priority hand users can still hide it. Attack-only visibility has
passed user testing. The adapter now adds native player combo/recovery breakout events after
the sword's hit window; the combo correction passed user testing. The player graph chooses
attacks by context, not a guaranteed fixed four-swing sequence. Allowing no-target attacks now
preserves each native state's target requirement rather than admitting every targeted opener
into empty-space selection; this correction passed user testing. Counters, takedowns and prop attacks retain their
player defaults; the item collision uses the selected native actor's, so custom
shapes may not match its hitbox. This is not a new selectable equipment-menu gadget.

First-time extraction and Full refresh include the katana, baseball-bat/stun-baton, closed umbrella,
baseball and smoke-bomb mesh examples and their direct material donors,
alongside the character/animation trees. Missing or incompatible donors block building with an
error instead of silently falling back to another weapon.

Previously built bat/baton player-adapter test ZIPs are unchanged and do not need remaking.
Their in-game-proven attack adaptation is now available through the normal fighting-style picker
and suit build, with independently configured held items. Choose the baseball-bat or stun-baton
held-item template for the corresponding tested actor/hitbox. Selecting only a model does not
change attacks. Reapplying the same preset keeps combat settings; changing presets restores the
new style's verified attack defaults and keeps the held items.

Baton is a melee adapter, not an automatic electrical power. The integrated bat/baton test paks,
held-item tool flow and separate native baton-trail experiment have passed user testing.
The new configurable effects and on-hit status paths below still need their own in-game acceptance.

### Cosmetic item effects

Open **Held items → Edit item → Edit effects / placement**. Add up to three native effects per
item, with mesh-local offsets in Unreal centimetres, pitch/yaw/roll and scale. The viewer shows
placement markers and approximate animated particles, including the custom model if one is assigned.
White markers identify emitter origins; axes show orientation. Particle shapes/colors are illustrative,
not final Niagara rendering or editable game color parameters. Placement overlays show through the mesh
so an emitter inside the model stays editable. Effects follow the held actor's visibility;
for attack-only effects, use an attack-only item. Independent effect timing is not implemented yet.

Twelve presets cover electric idle/trails, blade and baton trails, umbrella smear, baseball trail,
smoke, frost, snow, fire/sparks, venom and Ivy fumes. Except for the original baton-trail proof,
these combinations are experimental: some native systems need motion, owner parameters or context.
All referenced systems are included in first-time/full/research extraction. Use Full refresh if a donor
is missing. Builds fail on missing/non-Niagara donors rather than silently dropping the effect.

Effects are additional suit-local components; the game systems themselves are not overwritten.
When an item has configured effects, its original Niagara components are hidden/deactivated to avoid
duplicate native trails. Removing all configured effects restores the original actor behavior on rebuild.
No reflected Blueprint class fields or gameplay tags are added for these visual components.

### On-hit status settings (experimental)

**Combat settings → Behavior → On-hit status** adds timed **stun interruption** or **smoke distraction**
to the sword, bat and baton player adapters. Duration is 0.25–10 seconds; None is the default.
The attack's native damage effect is cloned locally and extended with a target-applied status.
Damage calculations, original hit timing and native target checks remain; no victim reaction ability
is granted to the wielder. Smoke is changed from volume-managed indefinite duration to a bounded duration.
Statuses require goon targets and reject playable/boss/dead tags; native smoke exclusions remain.

These use native AI interruption reactions, not a universal paralysis system. Target abilities,
immunities and repeated hits can affect the observed reaction/duration. Test a duplicate suit against
ordinary goons, resistant enemies and co-op before distribution. Electrical stun, poison damage,
freezing, arbitrary gameplay abilities, all native fighting styles and gadget projectiles are not
supported by this first status editor. Decorative items cannot apply statuses without a compatible attack.
Visual sparks/fire/frost do not automatically inflict an associated gameplay effect.

The same style on its shipped gameplay family is the reliable path. A cross-family application is
still experimental: Batcomputer copies only the traced combat effect, held-item bridge, and
animation dependencies instead of importing the donor's entire character AbilitySet. Packaging
stops if any required set, effect, held item, equipment entry, or animation parent cannot be proved
in the generated assets. Test every attack, traversal transition, equipment action, respawn, and
clean restart before sharing one of these combinations.

## Equipment and upgrades

Equipment edits preserve unchanged runtime slots, replace only the selected slot in both the
generated runtime data and menu metadata, and carry the selected equipment's matching upgrade data.
Batcomputer keeps controller AbilitySets on their equipment definition rather than duplicating them
onto the character. If the exact runtime slot or required controller cannot be read and verified,
the build is blocked instead of silently retaining the donor gadget.

## Recommended test flow

The Ability workshop separates the editable source loadout from coordinated fighting-style
dependencies. **Current loadout** shows added sets and grants, plus a staged bundle for generated
weapon attacks, the held-item ability, weapon actor and model. Bundle rows describe what the next
build generates; they are read-only, not extra editable donor sets. Use the style picker or
**Combat settings** for attacks and **Held items** for props. Adding an entry clears the previous search and selects the
new entry so it cannot remain hidden by a library filter. Search also matches input tags.

Combat settings are grouped into **Behavior** and **Attack sources**. Model, material and
visibility controls now belong to the separate held-item editor.
Changes are staged privately until you accept the settings and save the ability loadout.

For suits created from the legacy `Batman_Batman` donor, Batcomputer repairs its retired
`GameProgress.Definitions.Characters.Batman.Batman` unlock tag to the actual
`GameProgress.Definitions.Characters.Batman.TheBatman2025` progression entry on load/save.
Rebuild an affected suit and restart the game; existing installed containers are not changed
just by opening the project. Other unlock tags are preserved.

1. Duplicate a working suit and change one set or grant at a time.
2. Run **Check mod** and resolve every missing or incompatible dependency.
3. Build, fully restart the game, and test movement, damage, attacks, equipment, traversal, and
   respawning.
4. Use **Reset to donor** if the loadout becomes unstable.
