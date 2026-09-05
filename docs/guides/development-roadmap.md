# Development order

## 1. Finish the minifigure ability workflow (current)

- Implemented: sword, baseball-bat and baton attack adapters in normal suit builds, independently of held-item appearance and visibility.
- Verified offline: style switching, save/reload, suit-local packaging, required held-item checks and matching the approved bat/baton test timing.
- User-confirmed: integrated bat/baton tests, all seven prop/trail tests, held-item tool flow, custom models and left-hand items work.
- Expanded held-item examples: fourteen templates, including native small props and passive cosmetic options.
- Added configurable cosmetic VFX placement and approximate previews; twelve native presets. Added opt-in timed goon-target stun/smoke interruption for the player adapters. These new configurable paths still need separate in-game acceptance.
- Test independent held items in both hands, all visibility modes, custom models and gadget transitions.
- Trace and test baton VFX separately from hostile-target stun/shockwave behavior. Do not grant the entire enemy AbilitySet.
- Add further tuning only where its native meaning and player behavior are verified; speed and target requirement are the current adapter controls.
- Shield and hammer/BigFig adapters are out of scope.

Completion gate: normal in-tool builds pass checks and the changed combinations pass in-game acceptance. A successful build is not proof of runtime behavior.

## 2. Fix the next reported bug

The specific bug will be supplied after the ability pass. Reproduce and add coverage before moving on.

## 3. Finish custom equipment

Resume the parked equipment proof: its own assets/identity, discoverability, icon, usable controller/ability dependencies and upgrades. Keep independent held props distinct from selectable equipment.

## 4. Skinned meshes

Investigate supported skeletons, weights, bind pose, coordinate conversion and cooking. Prove a small compatible replacement before expanding import options.

## 5. Fully custom characters

Trace a complete new character identity, progression/registration, suit menus, gameplay and cutscene dependencies. Start with one minimal character and expand only after it survives restart, save/load and story use.

Later phases are planned, not implemented or implicitly authorized for this pass.
