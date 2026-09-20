"""Batcomputer icon set exporter for Blender.

The four source frames in the common LEGO character-icon template are:

    1  suit selector tile
    2  character facing right (authored/file name: Left)
    3  character front/menu portrait (authored/file name: Menu)
    4  character facing left (authored/file name: Right)

The left/right wording is intentionally documented this way. Batcomputer's UIMD
writer applies the game's native RightFacing/LeftFacing property convention.
"""

from __future__ import annotations

import os
import re
from pathlib import Path

import bpy
from bpy.props import BoolProperty, IntProperty, StringProperty


bl_info = {
    "name": "Batcomputer Icon Namer",
    "author": "Batcomputer",
    "version": (1, 0, 0),
    "blender": (3, 0, 0),
    "location": "View3D > Sidebar > Batcomputer Icons",
    "description": "Render or rename the four Blender character-icon frames for Batcomputer.",
    "category": "Render",
}


# Names are chosen to make Batcomputer's automatic icon-slot detection useful:
# "SuitIcon" wins the suit-selector score, while Menu/Left/Right identify the
# three 512px character portraits.
ICON_FRAMES = (
    (1, "suit", "T_UI_IconSuit_{name}_BCA.png"),
    (2, "left", "T_UI_IconChar_{name}_Left_BCA.png"),
    (3, "menu", "T_UI_IconChar_{name}_Menu_BCA.png"),
    (4, "right", "T_UI_IconChar_{name}_Right_BCA.png"),
)


def _safe_name(value: str) -> str:
    value = re.sub(r"[^A-Za-z0-9_ -]+", "", value).strip()
    value = re.sub(r"[ -]+", "_", value)
    return value or "Character"


def _output_dir(scene: bpy.types.Scene) -> Path:
    raw = scene.bc_icon_output_dir.strip() or "//BatcomputerIcons"
    return Path(bpy.path.abspath(raw)).resolve()


def _target_paths(scene: bpy.types.Scene) -> dict[int, Path]:
    name = _safe_name(scene.bc_icon_name)
    return {frame: _output_dir(scene) / pattern.format(name=name) for frame, _, pattern in ICON_FRAMES}


def _render_icon_set(scene: bpy.types.Scene) -> list[str]:
    destination = _output_dir(scene)
    destination.mkdir(parents=True, exist_ok=True)
    targets = _target_paths(scene)
    original_frame = scene.frame_current
    original_filepath = scene.render.filepath
    original_format = scene.render.image_settings.file_format
    messages: list[str] = []
    try:
        scene.render.image_settings.file_format = "PNG"
        for frame, role, _ in ICON_FRAMES:
            scene.frame_set(frame)
            target = targets[frame]
            scene.render.filepath = str(target)
            bpy.ops.render.render(write_still=True)
            messages.append(f"Frame {frame} ({role}) -> {target.name}")
    finally:
        scene.frame_set(original_frame)
        scene.render.filepath = original_filepath
        scene.render.image_settings.file_format = original_format
    return messages


def _rename_existing_frames(scene: bpy.types.Scene, overwrite: bool) -> list[str]:
    destination = _output_dir(scene)
    if not destination.is_dir():
        raise RuntimeError(f"Output folder does not exist: {destination}")

    targets = _target_paths(scene)
    # Blender's default animation names end in four digits, e.g. 0001.png or
    # render0001.png. Only those four expected frame numbers are considered.
    candidates: dict[int, Path] = {}
    for path in destination.iterdir():
        if not path.is_file() or path.suffix.lower() not in {".png", ".jpg", ".jpeg", ".exr"}:
            continue
        match = re.search(r"(?:^|[^0-9])(000[1-4])(?:\.[^.]+)$", path.name, re.IGNORECASE)
        if match:
            candidates[int(match.group(1))] = path

    missing = [str(frame) for frame, _, _ in ICON_FRAMES if frame not in candidates]
    if missing:
        raise RuntimeError("Could not find rendered frame(s): " + ", ".join(missing))

    # Use temporary names first so a rerun cannot collide when a source file is
    # already one of the semantic target names.
    staged: list[tuple[Path, Path]] = []
    for frame, _, _ in ICON_FRAMES:
        source = candidates[frame]
        temporary = destination / f".batcomputer_icon_tmp_{frame}{source.suffix.lower()}"
        if source != temporary:
            source.rename(temporary)
        staged.append((temporary, targets[frame]))

    changed: list[str] = []
    try:
        for temporary, target in staged:
            if target.exists() and target != temporary:
                if not overwrite:
                    raise RuntimeError(f"Target already exists: {target.name} (enable overwrite to replace it)")
                target.unlink()
            temporary.rename(target)
            changed.append(f"{temporary.name} -> {target.name}")
    except Exception:
        # Best-effort recovery keeps a failed rename from losing the staged files.
        for temporary, target in staged:
            if temporary.exists() and not target.exists():
                temporary.rename(candidates[next(frame for frame, _, _ in ICON_FRAMES if _target_paths(scene)[frame] == target)])
        raise
    return changed


class BC_OT_render_icon_set(bpy.types.Operator):
    bl_idname = "batcomputer.render_icon_set"
    bl_label = "Render Batcomputer Icon Set"
    bl_options = {"REGISTER"}

    def execute(self, context):
        try:
            for message in _render_icon_set(context.scene):
                self.report({"INFO"}, message)
            return {"FINISHED"}
        except Exception as exc:
            self.report({"ERROR"}, str(exc))
            return {"CANCELLED"}


class BC_OT_name_icon_set(bpy.types.Operator):
    bl_idname = "batcomputer.name_icon_set"
    bl_label = "Name Existing Frames"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        try:
            for message in _rename_existing_frames(context.scene, context.scene.bc_icon_overwrite):
                self.report({"INFO"}, message)
            return {"FINISHED"}
        except Exception as exc:
            self.report({"ERROR"}, str(exc))
            return {"CANCELLED"}


class BC_PT_icon_namer(bpy.types.Panel):
    bl_label = "Batcomputer Icons"
    bl_idname = "BC_PT_icon_namer"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Batcomputer"

    def draw(self, context):
        layout = self.layout
        scene = context.scene
        layout.prop(scene, "bc_icon_name", text="Character name")
        layout.prop(scene, "bc_icon_output_dir", text="Output folder")
        layout.separator()
        layout.operator(BC_OT_render_icon_set.bl_idname, icon="RENDER_STILL")
        layout.operator(BC_OT_name_icon_set.bl_idname, icon="SORTALPHA")
        layout.prop(scene, "bc_icon_overwrite", text="Overwrite existing")
        layout.separator()
        layout.label(text="1 Suit · 2 Right · 3 Front · 4 Left")
        layout.label(text="Right/left filenames follow game convention")


CLASSES = (BC_OT_render_icon_set, BC_OT_name_icon_set, BC_PT_icon_namer)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.bc_icon_name = StringProperty(name="Character name", default="MyCharacter")
    bpy.types.Scene.bc_icon_output_dir = StringProperty(
        name="Output folder", subtype="DIR_PATH", default="//BatcomputerIcons"
    )
    bpy.types.Scene.bc_icon_overwrite = BoolProperty(name="Overwrite existing", default=False)


def unregister():
    for prop in ("bc_icon_overwrite", "bc_icon_output_dir", "bc_icon_name"):
        if hasattr(bpy.types.Scene, prop):
            delattr(bpy.types.Scene, prop)
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
