# Batcomputer Blender icon namer

Install `batcomputer_icon_namer.py` in Blender with **Edit → Preferences → Add-ons → Install…**.
Enable **Batcomputer Icon Namer**, then open the 3D View sidebar (`N`) and choose the **Batcomputer** tab.

Set the character name and output folder. **Render Batcomputer Icon Set** renders frames 1–4 directly
to these filenames:

| Frame | Meaning | Filename role |
|---:|---|---|
| 1 | Suit selector tile | `T_UI_IconSuit_<name>_BCA.png` |
| 2 | Character facing right | `T_UI_IconChar_<name>_Left_BCA.png` |
| 3 | Front/menu portrait | `T_UI_IconChar_<name>_Menu_BCA.png` |
| 4 | Character facing left | `T_UI_IconChar_<name>_Right_BCA.png` |

The apparent left/right reversal is intentional: it matches the native UIMD convention used by
Batcomputer. **Name Existing Frames** is available when the four frames have already been rendered
as Blender animation files such as `0001.png` through `0004.png`.

Import frame 1 as **Suit selector icon** (256px) and frames 2–4 as **Character icon** (512px).
The script only names/renders files; Batcomputer still cooks the PNGs into game-ready Texture2D assets.
