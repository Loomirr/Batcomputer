"""Read-only animation import bridge. Run in Blender with --disable-autoexec.

Lists actions on a matching LOTDK body armature, or exports one action to a
disposable GLB. Never saves the source .blend; native viewer rest-space validation
is performed before the exported motion can replace an authoring draft.
"""
import json
import sys
from pathlib import Path

import bpy

config = json.loads(Path(sys.argv[sys.argv.index("--") + 1]).read_text(encoding="utf-8"))
expected = {part.split(":", 1)[0]: part.split(":", 1)[1] for part in config["rigSignature"].split("|")}
rigs = []
for obj in bpy.data.objects:
    if obj.type != "ARMATURE":
        continue
    actual = {bone.name: bone.parent.name if bone.parent else "" for bone in obj.data.bones}
    if actual == expected:
        rigs.append(obj)
if not rigs:
    raise RuntimeError("No armature matches this LOTDK body rig exactly. Retarget first; retain all native bone names and parents.")

if config["mode"] == "list":
    choices = []
    for rig in rigs:
        for action in bpy.data.actions:
            slots = list(action.slots) if hasattr(action, "slots") else []
            candidates = [slot for slot in slots if slot.target_id_type == "OBJECT"] or [None]
            for slot in candidates:
                choices.append({"rig": rig.name, "action": action.name,
                                "slot": slot.identifier if slot else "",
                                "label": action.name + " · " + rig.name + (" · " + slot.identifier if slot else "")})
    if not choices or len(choices) > 512:
        raise RuntimeError("Expected 1–512 animation action choices on the native rig.")
    Path(config["output"]).write_text(json.dumps(choices), encoding="utf-8")
else:
    rig = next(obj for obj in rigs if obj.name == config["rig"])
    action = bpy.data.actions.get(config["action"])
    if action is None:
        raise RuntimeError("The selected animation action is no longer available.")
    bpy.context.view_layer.objects.active = rig
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    rig.animation_data_create()
    rig.animation_data.action = action
    if config.get("slot"):
        rig.animation_data.action_slot = next(slot for slot in action.slots if slot.identifier == config["slot"])
    for track in rig.animation_data.nla_tracks:
        track.mute = True
    start, end = action.frame_range
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    if fps <= 0 or (end - start) / fps < 0.1 or (end - start) / fps > 30:
        raise RuntimeError("Choose an action between 0.1 and 30 seconds.")
    bpy.context.scene.frame_start = int(start)
    bpy.context.scene.frame_end = int(end)
    bpy.context.scene.frame_set(int(start))
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH" and
              any(mod.type == "ARMATURE" and mod.object == rig for mod in obj.modifiers)]
    if not meshes:
        raise RuntimeError("The native armature needs its bound body mesh for a reliable glTF animation export.")
    for obj in meshes:
        obj.select_set(True)
    kwargs = dict(filepath=config["output"], export_format="GLB", use_selection=True,
                  export_animations=True, export_skins=True, export_yup=True,
                  export_materials="NONE", export_cameras=False, export_lights=False,
                  export_def_bones=False, export_force_sampling=True)
    available = {prop.identifier for prop in bpy.ops.export_scene.gltf.get_rna_type().properties}
    if "export_animation_mode" in available:
        kwargs["export_animation_mode"] = "ACTIVE_ACTIONS"
    if "export_frame_range" in available:
        kwargs["export_frame_range"] = True
    if "export_optimize_animation_size" in available:
        kwargs["export_optimize_animation_size"] = False
    bpy.ops.export_scene.gltf(**kwargs)
    if not Path(config["output"]).is_file():
        raise RuntimeError("Blender did not produce the animation export.")
