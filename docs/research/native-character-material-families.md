# Native character material families and viewer coverage

Audited 2026-09-25 against the installed LEGO Batman: Legacy of the Dark Knight
pak files. This is a representative sample of base-game suits, heads, faces,
capes and effects, not a claim to have catalogued every material instance or
reproduced every Unreal shader permutation. No game assets are redistributed.

## What the cooked base game actually uses

| Base-game examples | Master family | Key observations | Current Batcomputer preview |
| --- | --- | --- | --- |
| `MI_Batman_89_EOM`, `MI_Batman_TheBatman2025`, `MI_Joker_Batman89_EoM` | `M_Char_EoM_Master` | Body artwork `BC`/`DNRM`/`MMR` is separate from `T_LEGOFIG_Norm` and `T_LEGOFIG_RAO`; `T_MicroNoise_Norm` is another input. | The body atlas, three normal inputs, MMR and structural RAO have separate GPU paths. Weathering, scratches, colour swaps and several other EoM stages remain approximations or unsupported. |
| `MI_HAT_BatmanCowl_MoldedEyes_EoM_B`, `MI_CAPE_Rubber_Black` | `M_Char_EoM_Master` | Same master, but part-specific `NRM`/`RAO` (`T_HAT_BatmanCowl_MoldedEyes_*`, `T_CAPE_Rubber_*`). Their `DNRM` and `MMR` resolve to dummy textures; those are not evidence of missing artwork. | Part slots can carry their own structural normal and RAO. Do not force the body LEGO normal onto a cowl or rubber cape. |
| `MI_CAPE_Spiked_Black17_LOD0` | `M_Cape_EoM` | Separate cloth/cape master, `T_Cape_Spiked_BC_EoM` and a different parameter set; its MMR can be dummy. | Cape preview is an approximation. It must not be calibrated using black plastic body roughness. |
| `MI_LEGO08_CAPE_Black` | `M_LEGO08_Cape` | Older cape master with `T_CAPE_Game08_NRM`, not the EoM body stack. | Legacy cape path needs a dedicated visual comparison. |
| `MI_FACE_Batman_NoEyes`, `MI_FACE_BruceAdult`, `MI_FACE_Joker_Batman89` | `M_LEGOface` | Complex feature zones with hundreds of resolved texture and scalar entries, including mouth/eyes and animated parameter controls. | Sampled native pose curves, feature zones, eye and mouth layers are previewed. The AnimBP, exact depth math and some compositing are not yet reproduced. Face-tab switches isolate printed zones, rim, upper/lower teeth and tongue without changing saved assets. |
| `MI_FaceTex_HAIRCOMBO_Robin_LBM`, `MI_FaceTex_KillerCroc` | `M_FaceTex_Static2` / `M_FaceTex_Static1` | Older face-texture family, distinct from `M_LEGOface`. Killer Croc has an MMR input. | Do not assume the LEGOface feature-zone/pose system applies to these. They need their own fixtures and acceptance screenshots. |
| `MI_Char_OverlayFrost_MrFreeze`, `MI_TranslucentOverlay_GhostGlitter01` | `M_Char_OverlayFrost` / `M_Char_TranslucentOverlay` | Effect/overlay masters rather than opaque minifig plastic. | No claim of shader parity; these need separate transparency/blending and effect tests. |

The EoM material chains include inherited controller/global-controller defaults.
The representative native body instances resolve roughly 55–57 textures,
121–125 scalars and 8–10 switches; the LEGOface examples resolve 276–290
textures and 225 scalars. Many are dummy or disabled inputs, but the counts
show why simply assigning a base colour, normal and roughness map cannot be
treated as a complete game-material reproduction.

## Electric diagnosis

The current Electric mod material follows the native EoM body pattern:
`DNRM` (authored decal), `NRM=T_LEGOFIG_Norm` (structural UV0),
`MicroNoise=T_MicroNoise_Norm` (tiled UV0), `MMR` (body atlas) and
`RAO=T_LEGOFIG_RAO` (structural UV0). The browser regression independently
disables each normal input and confirms that each changes rendered pixels.
Thus the current viewer is **not missing the LEGO normal texture**. It can
still look too smooth under its default studio light, or differ from the game
because its lighting, mip selection, material graph and post-processing are
not identical. The Scene tab now includes a *Surface detail* grazing-light
inspection preset; Studio remains the default, and the preset is not intended
to impersonate the Batcave lighting.

The Surfaces tab exposes separate authored/decal, LEGO structural and
micro-noise normal switches for materials that actually have those inputs.
This permits an A/B check without changing a material or project. The Face
tab separately exposes feature and mouth-component visibility. Expression
curves can still hide a checked feature; the checkboxes are inspection
overrides, not a replacement for runtime face animation.

## How to reproduce the audit

The read-only `--audit-preview-materials` CLI resolves material-instance
inheritance from a local game pak directory and mapping file. Results from
this run are machine-local under `artifacts/material-accuracy/native-taxonomy/`
and deliberately ignored by Git. A native body, a part, a cape, a LEGOface,
a legacy FaceTex and an overlay must all be checked before generalizing a
viewer shader rule. The local `SuitIconStudio.browser.cjs` and
`FaceParity.browser.cjs` checks exercise Electric's maps, face controls and
pose controls, but browser pixel changes alone do not prove a 1:1 game match.

Next validation should compare identical camera angles and controlled lighting
for one native body, one solid cowl/rubber part, one cloth cape, a LEGOface and
a FaceTex example. Only after those fixtures pass should unsupported EoM
stages or effect masters be implemented individually. Avoid global gloss or
normal-strength adjustments that merely conceal a mismatch in one suit.
