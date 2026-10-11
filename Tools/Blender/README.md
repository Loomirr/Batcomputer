# Batcomputer Blender icon namer

Install `batcomputer_icon_namer.py` in Blender with **Edit → Preferences → Add-ons → Install…**.
Enable **Batcomputer Icon Namer**, then open the 3D View sidebar (`N`) and choose the **Batcomputer** tab.

Open the authored icon template, including its character, camera animation and lighting. The add-on
does not replace the template's materials, lights, Cycles samples, compositor or color management.
Set the suit name and output folder. **Render Batcomputer Icon Set** renders frames 1–4 directly
to these filenames:

| Frame | Meaning | Filename role |
|---:|---|---|
| 1 | Suit selector tile | `<name>_Icon_suit.png` |
| 2 | Character facing right | `<name>_Icon_left.png` |
| 3 | Front/menu portrait | `<name>_Icon_menu.png` |
| 4 | Character facing left | `<name>_Icon_right.png` |

The apparent left/right reversal is intentional: it matches the native UIMD convention used by
Batcomputer. **Name Existing Frames** is available when the four frames have already been rendered
as PNG animation files such as `0001.png` through `0004.png` (legacy fixed-frame naming only).

These defaults match the **LoTDK Icon New** template's animated Suit Camera. Adjust the four frame
fields for other templates. **Render Selected Icon** exports just the chosen slot. Output is transparent
RGBA PNG at 256px for the suit and 512px for portraits. Scene frame, camera and output settings are
restored afterward, including on errors. Existing files are protected unless overwrite is enabled.
Install the updated script over the previous add-on and restart Blender before rendering.

Import frame 1 as **Suit selector icon** (256px) and frames 2–4 as **Character icon** (512px).
The script only names/renders files; Batcomputer still cooks the PNGs into game-ready Texture2D assets.
