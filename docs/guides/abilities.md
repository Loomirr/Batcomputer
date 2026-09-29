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
Test each new item/hand/visibility combination during gameplay and transitions before sharing it.

Visibility options are **Always held**, **Only while attacking**, **During combat or attacks**,
and **Hide during combat / attacks**. The last option also hides during empty-space attacks.
Native hand-slot priority and `Status.BlockItemGA` are retained for competing gadgets/actions.
For permanent cosmetic claws, the prop workspace also offers **Keep prop over animation
empty-hand requests** under **Hand priority · advanced**. This opt-in raises only that prop's
saved hand requests from Medium to Epic, above the native High-priority empty-hand animation
requests. It does not remove native abilities, bypass `Status.BlockItemGA`, or add hitboxes.
Other Epic-priority items can still compete, and props can overlap carried objects/gadgets;
test attacks, climbing, gadgets and transitions in-game. Leave it off for normal props.
Always/outside-combat items use independently registered request tags, not the native baton
request or animation-context tags. New combinations and hide-during-combat still need in-game testing.

An item does not grant attacks, change the fighting style or add electrical stun/shockwave
abilities. For weapon attacks, select **Sword**, **Baseball bat** or **Baton — player adapter** separately and add a compatible
right-hand item visible during attacks. Saving/building blocks an adapter configuration without
that item. Other styles can carry decorative props, with a warning about native weapon conflicts.
Cosmetic examples do not satisfy the adapter's required melee hitbox, even if the model looks like a weapon.

Choose **Right hand**, **Left hand**, or **Both hands** for an extra prop. Both hands uses two native
hand slots with one shared model/material/alignment/visibility recipe and occupies both extra-prop
slots. It does not mirror the geometry or grant new attacks. For different left/right grips or
models, use two separate single-hand props. Native attack items can still take priority.

Extra props open directly in a single model workspace from **Held items → Extra props → Edit prop**.
The **Prop** panel controls the name, hand (including Both hands), visibility and native behavior donor;
**Model**, **Align** and **Materials** control the imported mesh in that same window.
Import an OBJ, align it with draggable 3D handles or numeric position/rotation/scale controls,
toggle the original/custom models, and assign a cooked material package per OBJ material slot. **Validate & use prop**
checks a custom cooked mesh; accept Held items, save the Ability editor and rebuild the suit to package it. Source geometry
and alignment are stored in the suit project. The viewer resolves game materials under studio
lighting and uses mesh-local origin axes, not a calibrated hand-grip preview. Collision and hitboxes are
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
releases it afterward; higher-priority hand users can still hide it. The adapter adds native player combo/recovery breakout events after
the sword's hit window. The player graph chooses
attacks by context, not a guaranteed fixed four-swing sequence. Allowing no-target attacks now
preserves each native state's target requirement rather than admitting every targeted opener
into empty-space selection. Counters, takedowns and prop attacks retain their
player defaults; the item collision uses the selected native actor's, so custom
shapes may not match its hitbox. This is not a new selectable equipment-menu gadget.

First-time extraction and Full refresh include the katana, baseball-bat/stun-baton, closed umbrella,
baseball and smoke-bomb mesh examples and their direct material donors,
alongside the character/animation trees. Missing or incompatible donors block building with an
error instead of silently falling back to another weapon.

For the baseball-bat or baton adapter, choose the corresponding native held-item template as
well as the fighting style. Selecting only a model does not change attacks. Reapplying the same
preset keeps combat settings; changing presets restores the new style's attack defaults while
keeping independently configured items.

!!! warning "Effect/status experiments are on hold"
    Expanded held-item effects and on-hit statuses have caused suit-hover crashes. Do not enable
    them in a release mod. Keep **On-hit status** at **None** and leave additional effect lists empty
    while testing ordinary held items. Native actor effects are separate.

### Cosmetic item effects

The experimental **Edit effects / placement** preview uses illustrative particles and placement
markers, not Unreal's Niagara renderer. Seeing an effect in this preview is not evidence that it
is safe to package. A working native baton trail does not validate unrelated fire, frost or smoke
systems, which may require runtime owner parameters and controllers.

Select an effect in the list to attach the move/rotate/size handles to its placement marker.
These handles change only that effect's mesh-local placement, not the weapon's OBJ alignment.

### On-hit status settings (experimental)

Leave **Combat settings → Behavior → On-hit status** at **None** for normal builds. The experimental
status adapters are not a universal stun, freeze or poison system, and decorative particles do
not automatically inflict damage or a status. These experiments need separate fixes and testing.

The same style on its shipped gameplay family is the reliable path. A cross-family application is
still experimental: Batcomputer copies only the traced combat effect, held-item bridge, and
animation dependencies instead of importing the donor's entire character AbilitySet. Packaging
stops if any required set, effect, held item, equipment entry, or animation parent cannot be proved
in the generated assets. Test every attack, traversal transition, equipment action, respawn, and
clean restart before sharing one of these combinations.

## Replacing a character's native held items

Open **Abilities → Held items → Native items** to inspect managed actors from the active
ability grants. This is source-derived, not a list guessed from the character's name. Disabled
sets and removed grants are excluded; fighting-style held-item bridges are included. Each
managed slot and owned mesh component is listed separately. Catwoman's two claws, for example,
share a native actor but have independent left- and right-hand slots.

- **Edit appearance** accepts a cooked static mesh, or imports an OBJ directly into its item preview,
  assign its materials and align its geometry. Custom model materials belong in that editor.
- **Materials** lists surfaces and their assignments; choose a material for each surface.
  **Use original** clears the selected override and preserves the native component's material.
- **Override component placement** edits position in centimetres, rotation in degrees and
  per-axis scale, relative to the native parent. Leaving it off preserves native placement.
- **Hide visuals** clears only that component's mesh binding. It does not remove attacks,
  animation tags, the managed actor or its hand-slot request. Other components/effects on a
  multi-part actor can remain visible.
- **Reset changes** clears that binding's saved override. Edit both hands for matching claws.

The native draw/hide tags are displayed for inspection and retained, along with attack timing,
sockets and collision rules. Model size does not resize hitboxes. Builds clone the edited
controllers, actors and granting sets into the suit's namespace; base-game assets and other
characters are not replaced. Accept the Held items dialog, then save the Ability workshop and
rebuild the mod. Opening a dialog does not install anything or edit the donor.

Skinned/specialized meshes and actors without direct owned bindings are **inspect only**;
their rigs or dynamic visual logic need a dedicated adapter. **Scan details** reports missing
or unreadable active packages. This menu is not an exhaustive list of temporary actors spawned
by gameplay; equipment/gadgets still have their own Equipment workflow. If you switch to a
style that no longer grants an edited controller, restore its stale edit before building.

For a custom claw-equipped character using Catwoman as the donor, customize each `GA_Item_CatClaws` static mesh row
with the appropriate claw model. Start with native draw/hide behavior and test both hands,
attacks, gadgets, character switching and respawning in-game. Cooked roundtrip validation is
not a substitute for that runtime test.

### Models and material previews

The Held items browser separates **Native items** from **Extra props**. Search the native
list by item, ability or attachment slot. Select a row to see its timing, edit support and
technical identity; **Edit appearance** opens an isolated 3D item preview. Technical package paths
and native draw/hide tags are behind **Technical details**, separate from the appearance summary.

In the native-item editor, **Import custom OBJ** opens the file picker and loads your model in
the same viewer—no second workshop. **Materials** edits the active native or imported model's
slots. **Placement** provides live OBJ scale, centimeter offsets and degree rotations; use
the original-model checkbox to compare the meshes. Replacing the OBJ preserves
alignment and assignments for matching source material names. Canceling the picker changes nothing.

The advanced native component placement controls on that same panel override the transform
relative to the original parent, separately from OBJ alignment. Component placement is not
displayed in this isolated mesh preview; test the grip and attachment on the character or
in-game. **Hide visuals** retains native behavior. For a cooked mesh, **Use original** in Materials
clears only the selected slot override. **Use cooked mesh instead** discards the custom model
draft and restores the native mesh/material controls.

The weapon/equipment model workshop has a resizable preview and **Model**, **Align** and
**Materials** panels. Import the OBJ, align its centered mesh with the reference using scale,
centimeter offsets and degree rotations, then choose materials. **Choose material** searches both your
material library and game materials; enable **Your materials only** in the assignment panel to open
the picker scoped to your library. You can change that scope inside the picker. **Apply to all**
assigns the selection to every OBJ slot. Surface labels show the source OBJ material names.
Exact package paths remain available in the picker for advanced use. Materials are assigned
by stable OBJ slot identity, not by display name.

Previews use resolved color textures, normal maps, packed roughness/metalness, AO and applicable
LEGO/micro-detail layers—the same material resolver as the character viewer. They approximate
game shaders under studio lighting, not gameplay effects or exact in-game lighting. Imported
OBJs need authored UVs to display mapped textures correctly. Unavailable materials show a
preview error; choosing a material does not generate or repair it.

Both model workshops and the inline native-item editor share draggable 3D handles on the custom
OBJ's centered origin: **W** moves, **R** rotates, and **E** sizes. Use the on-screen tool buttons
when focus is in a numeric field. **World / Local axes** changes the handle orientation, not the
saved coordinate system; fields always use Unreal XYZ centimeters and Pitch / Yaw / Roll degrees.
World handle colors use the viewer basis: red X, green up (Unreal Z), blue depth (Unreal -Y).
Size is proportional; nonuniform stretching is not supported by the saved OBJ recipe. **Snap**
uses 1 cm movement, 15° rotation and 0.1 size increments. **Reset transform** restores unit size,
zero offsets and zero rotations. Numeric fields update when you release a drag, and numeric edits
update those same handles. Alignment changes reuse geometry instead of exporting on each movement.

Importing preserves both visibility choices and the original's size. The original is faded when
both models are shown; switch its appearance to materials or wireframe in the viewer. **Frame both**,
**Frame custom** and **Frame original** move only the camera, never the saved mesh. Dimensions in the
bottom readout are actual centimeters, not camera zoom. The original donor may be much larger than
your prop; for example, the plant-spray donor is not a claw-sized model.

OBJ files do not store units. **Align → Import size help** explicitly sets a starting multiplier
for centimeters, meters, millimeters or inches; it replaces the current scale without changing offsets
or rotations. **Match original size** explicitly matches the longest current bounding-box dimension,
and **Center on original** moves the custom model to the original's bounding-box center. Neither
changes the original. They are optional starting aids, not proof that a grip or hitbox is correct.

Press **F** to frame the selected camera target, or use the camera view, grid, axes and light controls.
Assign an imported OBJ's materials in its active **Materials** panel, not as native component overrides.
**Validate & use item** in the native-item editor (or **Validate & use model** in the separate
weapon/equipment workshop) bakes a private validation mesh before returning the recipe. Accept
the parent editors, save and rebuild to apply it. Canceling leaves the saved project untouched.

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
