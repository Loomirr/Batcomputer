# Suit icon studio

Open a suit in **View in 3D → Icon studio**. Choose Menu, Left-facing,
Right-facing (512×512) or Suit tile (256×256). Adjust zoom, vertical framing
and exposure, then **Save PNG**. The PNG has a transparent background; the
checkerboard is only a preview. The native save dialog confirms overwrites.

This first pass saves images for review. It does not cook textures, change UIMD
paths or overwrite project icons. The existing texture import/cooking workflow
is still required to use them in-game.

The studio renders a clone of the assembly, including native-visible parts even
when the viewer is temporarily isolated. Separate gliders, hidden helpers,
selection boxes and the floor grid are excluded. Actual material edits in the
viewer are included. Reload from the saved project to discard preview-only edits.

## Reference and limitations

Camera positions, lens lengths and emitter layout are derived from the supplied
`LoTDK Icon Temp.blend` scene: its frame-2 portrait and front-facing suit-tile
camera. Metres, axes and area-light sizes are converted for the viewer. The
opposite portrait is another camera view, not a mirrored texture.
The menu portrait is a separate front-facing view, not a duplicate of the right portrait.

The reference uses Eevee and Khronos PBR Neutral colour management; this viewer
uses three.js r128 and ACES. Lighting, reflections and game materials are an
approximation, not a pixel-identical render. Neutral-face limitations in the
character viewer also affect icons. Face animation is separate work.

No reference meshes, textures or `.blend` files are bundled with the app.
Blender is not needed to generate icons inside Batcomputer.

## Material diagnosis

An MMR is packed data, not a colour image. The preview repacks it to an ORM
texture for three.js; the orange-looking ORM must not be compared visually
with the red/blue source MMR. Compare the raw decoded pixels instead.

Generated material instances are resolved before their parent controllers,
and the saved project's latest texture cooks take precedence over stale
whole-suit staging copies. EoM body/attachment materials also use structural
RAO on UV0 (R: roughness, G: ambient occlusion), independently of the decal
atlas UV. The roughness blend follows the supplied Blender setup; it is still
an approximation of the game's shader, without weather/grease simulation.
Materials without an authored MMR must not borrow a different part's map.

The viewer's material details show the resolved material, MMR-to-ORM and RAO
paths. **MMR / roughness** and **Ambient occlusion** can be toggled separately
to inspect their contribution. None of these preview switches changes cooked assets.

## Developer checks

- `inspect_scene.py`: read camera, lights and colour settings in background
  Blender. Run with `--factory-startup --disable-autoexec` and supply the output
  JSON path after `--`. Does not save the source `.blend`.
- `render_reference.py`: render an exported assembly inside the original
  template for visual comparison. Supply the assembly GLB and output PNG after
  `--`. Does not save the source `.blend`.
- `node Tools/Icons/SuitIconStudio.browser.cjs <generated-preview-folder>`:
  copies the input into `artifacts/suit-icon-test`, renders all four PNGs and
  tests dimensions, transparency, facing views, reset, narrow layout, reopening,
  isolation and live-scene preservation. Requires Playwright and Edge.
  The Electric fixture additionally checks material provenance, linear mask
  sampling, structural maps and visible/restorable surface toggles.
- `inspect_materials.py`: inspect the reference Blender material graph without
  modifying the blend file.
- `Batcomputer --audit-preview-materials <paks> <usmap> <output> <loose-content> <material-path> [...]`:
  write resolved material chains/parameters and raw MMR/RAO PNGs for diagnosis.
- `Batcomputer --verify-release-regressions`: includes same-origin PNG and GLB
  download allow-list checks.

The offline area-light helper is pinned to three.js r128. Preserve its MIT and
LTC lookup-data notices when distributing the app.
