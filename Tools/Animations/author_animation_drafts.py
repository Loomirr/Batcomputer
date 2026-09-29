"""Author native-rig animation drafts in an isolated Unreal 5.6 cook project.

This script never modifies the game or a saved suit. The caller validates and
imports the resulting cooked sequence into Batcomputer's animation library.
"""
import json
import math
from pathlib import Path
import unreal

project_root = Path(unreal.Paths.project_dir())
config = json.loads((project_root / "import.json").read_text(encoding="utf-8"))
# The established skeletal importer rejects a mismatched rest rig and normalizes its
# known x100 FBX root-unit representation before this script authors any animation.
exec((project_root / "import_mesh.py").read_text(encoding="utf-8"))
rig = {bone["name"]: bone for bone in config["donor_bones"]}
rig_signature = "|".join(
    bone["name"] + ":" + (config["donor_bones"][bone["parent"]]["name"] if bone["parent"] >= 0 else "")
    for bone in config["donor_bones"]
)

def normalized(q):
    size = math.sqrt(sum(v * v for v in q))
    if size < 1e-9:
        raise ValueError("Zero quaternion")
    return [v / size for v in q]

def multiply(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return normalized([
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    ])

def interpolate(keys, frame):
    before = {"frame": 0, "p": [0, 0, 0], "q": [0, 0, 0, 1], "s": [1, 1, 1]}
    after = keys[-1]
    for key in keys:
        if key["frame"] >= frame:
            after = key
            break
        before = key
    if after["frame"] == frame:
        return after["p"], normalized(after["q"]), after.get("s", [1, 1, 1])
    if after["frame"] == before["frame"]:
        return before["p"], normalized(before["q"]), before.get("s", [1, 1, 1])
    alpha = (frame - before["frame"]) / (after["frame"] - before["frame"])
    transition = before.get("interpolation", "linear")
    if transition == "hold":
        alpha = 0
    elif transition == "smooth":
        alpha = alpha * alpha * (3 - 2 * alpha)
    elif transition != "linear":
        raise ValueError("Unsupported key transition: " + transition)
    position = [a + (b - a) * alpha for a, b in zip(before["p"], after["p"])]
    scale = [a + (b - a) * alpha for a, b in zip(before.get("s", [1, 1, 1]), after.get("s", [1, 1, 1]))]
    qa, qb = normalized(before["q"]), normalized(after["q"])
    dot = sum(a * b for a, b in zip(qa, qb))
    if dot < 0:
        qb = [-v for v in qb]
        dot = -dot
    if dot > .9995:
        rotation = normalized([a + (b - a) * alpha for a, b in zip(qa, qb)])
    else:
        theta = math.acos(max(-1, min(1, dot)))
        factor = math.sin(theta)
        rotation = [
            (math.sin((1 - alpha) * theta) * a + math.sin(alpha * theta) * b) / factor
            for a, b in zip(qa, qb)
        ]
    return position, rotation, scale

asset_tools = unreal.AssetToolsHelpers.get_asset_tools()
# A transform-only SkeletonModifier edit normalizes the mesh, not its USkeleton.
# The caller separately validates the actual normalized mesh and the sole known
# imported Root x100 metadata artifact, remaps by USkeleton names, then decodes
# every cooked frame against this draft on the native runtime skeleton.
factory = unreal.AnimSequenceFactory()
factory.set_editor_property("target_skeleton", mesh.get_editor_property("skeleton"))
factory.set_editor_property("preview_skeletal_mesh", mesh)
results = []
for source in config["drafts"]:
    draft = json.loads(Path(source).read_text(encoding="utf-8"))
    if draft["schema"] != "batcomputer.animation-draft.v1" or draft["fps"] != 30 or draft["rigSignature"] != rig_signature:
        raise RuntimeError("Draft rig/fps mismatch: " + source)
    frames = draft["durationFrames"]
    if frames < 3 or frames > 900 or not draft["tracks"]:
        raise RuntimeError("Invalid draft length or empty tracks: " + source)
    name = config.get("asset_name") or "A_" + draft["name"]
    folder = config.get("animation_folder", "/Game/Mods/AnimationCreatorIdleTest/Animations")
    if not name.startswith("A_") or not name.replace("_", "").isalnum() or not folder.startswith("/Game/Mods/"):
        raise RuntimeError("Unsafe animation asset name or package folder")
    sequence = asset_tools.create_asset(name, folder, unreal.AnimSequence, factory)
    if not sequence:
        raise RuntimeError("Could not create " + name)
    controller = sequence.get_editor_property("controller")
    controller.open_bracket("Author Batcomputer idle test", False)
    try:
        controller.set_frame_rate(unreal.FrameRate(numerator=30, denominator=1), False)
        controller.set_number_of_frames(unreal.FrameNumber(value=frames), False)
        for track in draft["tracks"]:
            bone_name = track["bone"]
            if bone_name not in rig:
                raise RuntimeError("Unknown native bone: " + bone_name)
            base = rig[bone_name]
            keys = track["keys"]
            if not keys:
                raise RuntimeError("Animation track is empty: " + bone_name)
            if not controller.add_bone_curve(bone_name, False):
                raise RuntimeError("Could not add bone track: " + bone_name)
            positions, rotations, scales = [], [], []
            for index in range(frames + 1):
                viewer_position, viewer_q, viewer_scale = interpolate(keys, index)
                # Native Unreal -> viewer is an X-preserving Y/Z reflection; quaternion
                # XYZ is axial, so all three components change sign when swapping Y/Z.
                delta = [-viewer_q[0], -viewer_q[2], -viewer_q[1], viewer_q[3]]
                q = multiply(base["rotation"], delta)
                p = base["translation"]
                positions.append(unreal.Vector(p[0] + viewer_position[0] * 100,
                                               p[1] + viewer_position[2] * 100,
                                               p[2] + viewer_position[1] * 100))
                rotations.append(unreal.Quat(*q))
                # Viewer Y/Z axes are reflected into Unreal's Z/Y axes, respectively.
                scales.append(unreal.Vector(base["scale"][0] * viewer_scale[0],
                                             base["scale"][1] * viewer_scale[2],
                                             base["scale"][2] * viewer_scale[1]))
            if not controller.set_bone_track_keys(bone_name, positions, rotations, scales, False):
                raise RuntimeError("Could not write bone keys: " + bone_name)
    finally:
        controller.close_bracket(False)
    sequence.set_editor_property("loop", bool(draft.get("loop", False)))
    if not unreal.EditorAssetLibrary.save_loaded_asset(sequence):
        raise RuntimeError("Could not save " + name)
    results.append({"name": name, "package": sequence.get_path_name(), "frames": frames,
                    "tracks": [track["bone"] for track in draft["tracks"]],
                    "skeleton": sequence.get_editor_property("skeleton").get_path_name()})

(project_root / "animations-result.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
unreal.log("Batcomputer: authored %d test AnimSequences on validated imported native rig." % len(results))
