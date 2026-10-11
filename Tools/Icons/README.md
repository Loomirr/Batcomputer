# Character Icon Studio

Open a saved suit in **View in 3D → Icon studio**. Blender is not required to generate icons.
The three character portraits use 512×512 pixels; the separate suit tile uses 256×256.

## Create icons

1. Select Menu, Left slot, Right slot or Suit tile. Adjust that slot's camera framing.
2. Adjust shared blue rim, warm rim, white/key light and exposure as needed.
3. Choose **Create and assign selected icon**, or **Create and assign all character icons**.
4. Save/rebuild the mod and check hover and selection in-game.

All requested cooks must succeed before a batch assignment is saved. **Create selected PNG…**
exports artwork for manual editing without changing project assignments. **Test suit icon cook**
is a separate dry run and never installs a mod. The checkerboard indicates transparency; it is
not part of the exported image. Use **Actual pixels** to judge the small suit tile fairly.

Each slot keeps its own camera. The left/right labels refer to game destinations, not the direction
the portrait faces; views are not mirrored textures. All four use the same captured pose.
**Refresh captured pose** takes a new snapshot from the main viewer.

## Share settings

Use **Share a studio template → Export settings JSON…** and send the `.icon-studio.json` file.
**Import settings JSON…** loads its cameras and lighting, not character assets or assigned textures.
Create and assign new icons after importing the settings.

## Rendering limits

The studio uses template-derived cameras and lighting with a PBR Neutral response. Game materials,
shadows and reflections remain approximations; output is not guaranteed to match Blender or Unreal
pixel for pixel. Separate gliders, helper geometry and selection overlays are excluded from icons.
Reference meshes, textures and Blender templates are not bundled with this tool.

See the [full icon guide](../../docs/guides/materials-textures-faces.md#ui-icons) for texture profiles
and the distinction between portraits, character emblems and equipment SDF icons.

## Contributor utilities

- `inspect_scene.py`: inspect a locally supplied Blender template's camera/light/color settings.
- `render_reference.py`: compare an exported assembly with a local reference scene.
- `inspect_materials.py`: inspect reference material inputs.
- `SuitIconStudio.browser.cjs`, `FaceParity.browser.cjs`: browser regression checks for studio and face rendering.

These are development utilities, not prerequisites for Icon Studio. Keep source templates private
unless their redistribution is permitted, and run Blender with auto-execution disabled when inspecting
untrusted files. Read each script's arguments before running it; do not assume it operates on the
currently open Batcomputer project.
