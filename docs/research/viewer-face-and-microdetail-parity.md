# Viewer face and micro-detail investigation

Investigated and partly implemented 2026-09-25 against the installed game paks.
No game files, installed probes or saved projects were changed. Application code
was changed; this remains a viewer approximation, not a claim of game-shader parity.

## Current offline result

- Electric's authored decal normal, raw LEGO structural normal and tiled micro
  normal are separate shader samples. The cooked material resolves
  `T_MicroNoise_Norm`, UV0 tile 80 and strength 0.1; the cowl has its own settings.
  The Electric icon regression verifies all three inputs and toggles the MMR/RAO
  render path. A separate normal-layer A/B check confirms that each input
  changes rendered pixels and restores independently. The Scene tab has a
  grazing-light *Surface detail* inspection preset; it does not alter the
  default Studio view or claim to reproduce game lighting.
- Face-zone switches now apply child overrides over parent defaults and parse
  numbered-only zones. The mouth shader retains the intended 0.01 UV offset,
  and face material rebinding keeps its custom shader hooks.
- Six samples from each of 16 compatible native LEGOface clips now drive matching
  bone transforms and material curves in the Face inspector. The selector is a
  pose check, not continuous animation playback. Bruce's own
  `A_Idle_BruceWayne_LEGOface` is preferred for neutral; Batman resolves his
  own neutral clip. Both render with 54 matched face bones.
  Cooked `*hide` and `*show` curves are also evaluated against matching face
  layers. On Bruce, the Closed clip sets upper-eyelid show near zero and
  lower-eyelid show near one; however those eyelid zones are not enabled by his
  material, so their individual visibility cannot be visually validated there.
  A browser check does confirm that selecting Closed changes Bruce's rendered
  face and that returning to Neutral restores the initial render.
  A double application of the compressed-track mapping was found by comparing
  left/right eye transforms: it put `Eye_R` at the origin. The corrected browser
  render shows both eyes. Icons choose a fixed sampled neutral frame and restore
  the user's viewer expression afterward. The Face tab can temporarily
  show/hide printed feature zones, mouth line, upper/lower teeth and tongue.
  Whole attachments use Assembly's Hide part. These viewer-only switches
  persist across sampled pose changes; curves can still hide checked features.
- This does **not** reproduce the full runtime face AnimBP. The chosen neutral
  sample, eye highlights, feature depth, mouth layers, other face families and
  game lighting still need visual comparison with the game. Bruce's current
  eye highlight and mouth interior still look wrong even though both eyes are
  now present. Band isolation shows the extra lower red arc comes from
  `HeadLowerUnder` (zone 8), while the thin light edge comes from the mouth's
  teeth/tongue layers (zone 13); they are not the same artifact. Do not hide
  either layer globally without a native visual reference.

## Confirmed findings

### Body micro-detail: repaired for EoM preview materials

The Electric preview log reports `T_LEGOFIG_Norm` with `T_MicroNoise_Norm`,
strength `0.1`, tile `80`. The resolved material has
`MicroDetailSystem_On/Off=true`, `Micro Detail Intensity=1`,
`1Red_Noise_Scale=80`, and `1Red_Noise_Strength=0.1`.

Previously `ResolveStructuralNormal` / `BakeNoisedNrm` collapsed LEGO and micro
detail into one baked map, prefiltering the 256x256 noise to 16x16. The EoM path
now exports the raw LEGO normal and micro map separately and lets the GPU select
the mip at render distance. Non-EoM materials still use their previous path.
The resolved material exposes additional noise channels and metal-specific
settings that are not yet reproduced; increasing global normal strength would
not address those differences.

### Face expressions: sampled neutral path connected

`LoadFacePose` is connected for the 16 sampled LEGOface expressions and rejects
a different skeleton.
The browser checks exercise both a cowled and an unmasked LEGOface, including
the right eye's nonzero, mirrored transform. This is a fixed six-sample clip,
not continuous animation playback or proof that the game's face AnimBP chooses
that exact source/time in every context. The static face profile remains a
material baseline; it is not substituted for a bone pose.

### Confirmed shader bugs: repaired

1. `FACE_UV_OFFSET_UNIT` was `0.01` but serialized with `toFixed(1)`, producing
   `0.0`. It now retains two decimal places. The intended unit still needs
   controlled comparison with the game.
2. Three.js r128 `material.clone()` retains `skinning` but drops
   `onBeforeCompile` and `customProgramCacheKey`. Rebinding now preserves these
   hooks and keeps the material-editor state aimed at the rendered material.
   Headless browser checks verify the mouth shader survives the first draw.

These are independently reproducible code defects, not unverified shader guesses.

### Parameter resolution and layering still contain approximations

- Child false now overrides parent true for face-zone and tint switches; the
  numbered-only zone parser is covered by a release regression.
- The current game exposes 225 resolved scalars for both Batman NoEyes and
  BruceAdult through `CMaterialParams2`. Face scalar lookup now uses that
  resolved view before older captured baselines.
- Current Batman defaults include Mouth PDO 0.55, HeadLowerUnder PDO 2.5,
  PDO Fresnel Multiplier 1 and Exponent 5. The viewer now reads resolved
  per-feature PDO scalars where possible but depth handling remains an
  approximation. Parameter availability does not establish the full formula.
- Face profiles were keyed only by material path. The Electric capture exposed
  that a Golden Age Batman capture shared `MI_FACE_Batman_NoEyes`, so its live
  scalar snapshot could be borrowed for Electric. Lookup now requires the
  exact visual Blueprint, face mesh and material. This prevents cross-character
  contamination; it does not make a one-shot snapshot a universal neutral.
- The current `M_LEGOface` package has one Material export and no editable
  expression exports in the inspected cooked package. Many parameter defaults
  survive, but the shader graph is not available through this asset inspection.

## Remaining repair order

1. **Resolved face recipe:** preserve raw linear colours and parameter names,
   distinguish explicit overrides, inherited defaults and unknowns. Resolve
   mesh/skeleton/AnimBP, textures, masks, UV controls and layer visibility as one
   recipe. Do not replace unknowns silently with another character's artwork.
2. **Neutral-source verification:** compare the sampled sequence and time to
   the actual face AnimBP's effective idle pose; test more rig families.
3. **Layer fidelity:** verify eye/pupil/highlight composition, mouth/teeth/tongue
   ordering, atlas cells, alpha clipping, normal orientation and depth behavior.
4. **Icon contract:** the icon path currently applies a sampled neutral frame
   temporarily and restores the live viewer; compare with game icon examples
   and show an honest warning for unsupported face families.
5. **Animation:** after neutral passes, implement continuous time sampling and
   pose/curve synchronization, then blends/idle behavior. Do not label a handful
   of sampled poses as the full game's animation system.

Micro-detail can be developed independently, but its near/far mip behavior must
be tested in both the viewer and icon renderer.

### Gloss and surface fidelity

Electric's MMR is decoded and bound: its blue source channel becomes the
preview's green roughness channel, and its red source channel becomes blue
metalness. The EoM preview also binds `T_LEGOFIG_RAO` on UV0, plus the
separate decal, LEGO structural and micro-noise normals. This rules out a
simply missing MMR as the explanation for the glossy close-up. The decoded
Electric roughness texture has a median green value around 78/255; the
structural RAO red channel is around 63/255. Those are genuinely low
roughness inputs. The viewer additionally uses a bright four-light studio and
simple gradient environment. It does not yet reconstruct all of the cooked
EoM material's grime, scratch, multi-channel noise, weather or plastic/metal
response. Changing roughness globally or turning off highlights would hide
the discrepancy, not establish parity. Next compare the same surface under
matched camera/lighting in-game, then implement and test one missing native
material stage at a time.

### Electric in-game comparison, 2026-09-25

The supplied Batcave front, torso, leg and face screenshots compare against
Batcomputer captures of the same Electric suit. The live
`face-baseline_4B276DBE23BCFD60_227C1EB570153F7F.json` reports status `ok`:

- `CharacterMesh0` is `/Game/Characters/LEGOfig/SK_LEGOfig_Minifig` with
  `/Game/Mods/ElectricLBM2/MI_Batman_ElectricLBM2_Body`; its dynamic material
  has no texture, scalar or vector overrides.
- `Head` is `SK_HAT_Batman_TheBatman`, slot 0 uses the Electric cowl material,
  and slot 1 uses `MI_BatmanCowlEyes_Hollow`. The only reported dynamic scalar
  on these slots is `CowlFade=0`.
- `Face` is `SK_LEGOface` with `MI_FACE_Batman_NoEyes`. Its dynamic material
  has 34 scalar and 6 vector overrides. The scalar snapshot includes
  `MouthHide=0`, upper eyelid show=1, lower eyelid show≈0, and animated
  teeth/tongue UV offsets and scales. This capture has `game_build=unavailable`
  and does not include evaluated face bone transforms or a synchronized frame.

The viewer is visibly brighter and more uniformly glossy on the black body,
arm and cowl, and its blue graphics are brighter than the Batcave reference.
Because the body has no live overrides, this is not explained by an unrecorded
runtime body-material swap. The game image has warm/orange environmental light,
however, so a single global roughness change cannot be inferred from these
screenshots alone. Compare a second lighting context before calibrating any
material stage. The leg/hand edge and cape texture also deserve separate
geometry/normal/cloth checks, not a generic roughness adjustment.

The viewer face shows bright orange cheek marks, a red lower arc and a thin
light edge on the mouth; the game face has subdued brown marks and no such
light edge in the supplied frame. The same named face and cowl materials are
used. Our existing layer isolation attributes the lower marks to
`HeadLowerUnder` and the light edge to the teeth/tongue composition on `Mouth`.
One potential source of wrong fallback mouth offsets was the bundled August capture
for Golden Age Batman being selected by material path alone. That capture and
Electric share `MI_FACE_Batman_NoEyes` but are different character Blueprints.
Exact-character matching now prevents this fallback. A regenerated native-Batman
neutral render did not visibly change, because the sampled animation curves
override these fallback values for that pose. This is an identity-safety fix,
not a demonstrated fix for Electric's bright mouth edge. The visible mismatch
still needs renderer and synchronized-pose work.
The live scalar offsets differ slightly from the six sampled native-neutral
frames, confirming the current fixed sample is not a guaranteed match to the
game's animated face at this instant. Next capture the final face bone-local
transforms and effective dynamic material values *together* with a frame/time
marker; do not bake this one-shot snapshot in as the universal neutral pose.

### Saved Electric project: Visual Studio versus isolated preview

The Visual Studio Debug executable was newer than the isolated test build, so
the missing detail was not caused by an old executable. The two builds took
different material-resolution paths. The Debug preview found Electric's loose
generated material instance, but could not follow its cooked parent chain to
the EoM controller. It therefore used the child textures without the inherited
`T_LEGOFIG_Norm`, `T_MicroNoise_Norm`, or `T_LEGOFIG_RAO`. The isolated test
selected the controller fallback and retained those inherited maps. Preview
resolution now uses the generated material's known parent controller when the
loose asset cannot prove the EoM chain, while preserving its explicit child
texture, color, and scalar overrides. A regenerated Debug preview of the same
saved suit project now resolves the same body model and five body map PNGs as
the isolated test (matching SHA-256 hashes).

The earlier repeated `no usable MMR` lines came from helper, cape, and part
slots; Electric's body MMR had decoded successfully earlier in that log. The
current build omits that misleading message for slots with no requested
non-dummy MMR and reports the body's resolved maps together.

The fixed preview of the *saved suit project*, not merely its installed pak,
resolves `T_Batman_ElectricLBM2_NRM` (decal), `T_LEGOFIG_Norm` (structural),
`T_MicroNoise_Norm` (tile 80, strength 0.1), the cooked Electric MMR, and
`T_LEGOFIG_RAO`. Disabling each normal layer changes the close-up render; at
256px icon distance the micro layer affects far fewer pixels than the LEGO
normal, consistent with a fine tiled layer being filtered down. The saved
project uses `SK_Legofig_Minifig_08` (4,458 exported body vertices), whereas
the sampled native 1989 playable uses `SK_LEGOfig_Minifig` (10,961 vertices).
Among shared vertex positions, 97.5% had the same UV0 to 0.003, so an entirely
different structural UV layout is not supported by this check. The lower-poly
body can change silhouette and geometric highlights, but does not establish a
missing normal map. Regenerate the viewer preview after relaunching the Debug
build to replace any previously generated snapshot.

## Runtime probes: measure relationships, not one screenshot

The existing Developer Tools face-baseline capture is one-shot and useful for
component/material identity. It is not a complete expression capture: its
animation envelope reads the body component and does not include the final face
bone pose. The old August captures also record `game_build=unavailable`.

First implement a **manually triggered, bounded, read-only time-series capture**.
Do not install it or change the running game as part of this research step.
An initial design is a short burst at a capped sampling rate, with explicit
frame/time stamps and an availability status for every unsupported field.

Capture together, after animation evaluation where accessible:

- Game build, face mesh/skeleton/material/AnimBP identities and asset fingerprints.
- Actual face component attachment, relative transform and visibility.
- Face animation instance, active sequences/montages, times, weights and named
  curves when exposed; do not substitute the body's animation instance.
- Final face bone-local transforms and required component/parent transforms;
  include scale. Never mix live component transforms with reference-pose data.
- Effective dynamic material scalar/vector/texture values, not just the
  instance's raw override arrays. Record failed/unavailable queries honestly.
- A matching screenshot or video frame and enough camera/lighting context to
  separate geometry/material errors from different lighting.

Capture quiet idle, a blink, a mouth-open state and a return to idle. Repeat
across representative face families. Compare deltas over time and match them
to cooked animation curves. Repetition identifies transient blink/talk values
and prevents an arbitrary gameplay instant becoming the permanent neutral pose.

If essential shader math remains unknown, a separate opt-in controlled material
parameter sweep may be useful. That changes temporary in-game state and needs
explicit approval, isolation and restoration; it is not part of the read-only
capture. Shader/frame inspection is another later option, not evidence already
obtained here.

## Acceptance tests

- Start with cowled Batman/Electric, unmasked Bruce, and a different family such
  as Joker or a superhero face. Add more families after identifying their rigs.
- Child false wins over parent true; numbered zones are handled explicitly.
- Changing each mouth UV offset changes pixels; restoring it restores pixels.
- Mouth/eye shader extensions survive first draw, material refresh, isolation,
  icon cloning and repeated exports. Editor toggles address the live material.
- Neutral renders are deterministic across reloads and do not depend on when a
  gameplay screenshot was captured. Wrong rig/build profiles are rejected.
- Bone pose and material curves use the same time; blink/talking do not leave
  stale teeth, pupils or tongue when returning to neutral.
- Compare front and three-quarter close-ups, with and without cowl; no floating
  layers, duplicate mouths or disappearing features at grazing angles.
- Toggle authored normal, LEGO normal and micro-detail independently. Verify
  close-up detail without crawling/shimmer at full-character and icon distances.

Exact 1:1 rendering is not established. A reproducible sampled neutral face
and three-layer EoM normal path are now implemented; game lighting, shader
permutations and animation-graph behavior remain separate validation work.
See [native character material families](native-character-material-families.md)
for the base-game body, cowl, cape, face and overlay audit behind this scope.

## Local evidence

Ignored, machine-local diagnostic outputs:

- `artifacts/material-accuracy/fixed.log`
- `artifacts/material-accuracy/native-parameters/MI_Batman_ElectricLBM2_Body.json`
- `artifacts/material-accuracy/face-audit/MI_FACE_Batman_NoEyes.json`
- `artifacts/material-accuracy/face-audit/MI_FACE_BruceAdult.json`
- `artifacts/material-accuracy/face-master-shape.log`
- `artifacts/material-accuracy/face-browser/Bruce-checks.json`
- `artifacts/material-accuracy/face-browser/Batman-checks.json`
- `artifacts/suit-icon-test/face-and-normal-checks.json`

Older research under the parent workspace's `docs/face-system-deep-dive-2026-07-26.md`
and face-baseline capture notes is useful history, not proof of the current
renderer or current game build. Several old assumptions need revalidation.
