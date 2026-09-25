"""Headless UE 5.6 import. Input JSON is data, never Python code supplied by the FBX."""
import json
import math
from pathlib import Path
import unreal

root = Path(unreal.Paths.project_dir())
config = json.loads((root / "import.json").read_text(encoding="utf-8-sig"))
unreal.SystemLibrary.execute_console_command(None, "Interchange.FeatureFlags.Import.FBX 0")
options = unreal.FbxImportUI()
for key, value in {
    "automated_import_should_detect_type": False,
    "mesh_type_to_import": unreal.FBXImportType.FBXIT_SKELETAL_MESH,
    "import_as_skeletal": True, "import_mesh": True,
    "import_animations": False, "import_materials": False,
    "import_textures": False, "create_physics_asset": False,
}.items():
    options.set_editor_property(key, value)
data = options.get_editor_property("skeletal_mesh_import_data")
for key, value in {
    "import_morph_targets": False, "update_skeleton_reference_pose": False,
    "use_t0_as_ref_pose": False, "import_uniform_scale": config["scale"],
    "normal_import_method": unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS,
}.items():
    data.set_editor_property(key, value)
task = unreal.AssetImportTask()
task.filename = config["source"]
task.destination_path = config["package"].rsplit("/", 1)[0]
task.destination_name = config["package"].rsplit("/", 1)[1]
task.automated = True
task.replace_existing = False
task.save = True
task.options = options
unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
meshes = [unreal.load_asset(p) for p in task.imported_object_paths]
meshes = [m for m in meshes if isinstance(m, unreal.SkeletalMesh)]
if len(meshes) != 1:
    raise RuntimeError("Expected exactly one imported skeletal mesh.")
mesh = meshes[0]
# Imported root-unit scales are real hierarchy transforms, not harmless metadata.
# Normalize only the x100 FBX conversion when every other native local pose agrees.
# Unreal's modifier rebuilds the inverse bind matrices from the corrected hierarchy.
modifier = unreal.SkeletonModifier()
if not modifier.set_skeletal_mesh(mesh):
    raise RuntimeError("Could not inspect the imported reference skeleton.")
expected = config["donor_bones"]
names = [str(n) for n in modifier.get_all_bone_names()]
if len(names) != len(expected) or set(names) != {b["name"] for b in expected}:
    raise RuntimeError("Imported bones do not match the native donor. Keep the complete native rig and remove exporter helper/leaf bones.")
root_correction = None
poses = {name: modifier.get_bone_transform(name, False) for name in names}
native_root = next(b for b in expected if b["parent"] == -1)
root_scale = poses[native_root["name"]].scale3d
has_root_units = all(abs(v - 100.0) < 0.0001 for v in (root_scale.x, root_scale.y, root_scale.z)) and all(abs(v - 1.0) < 0.00001 for v in native_root["scale"])
def translation_error(factor):
    return max(math.dist(tuple(v * factor for v in (poses[b["name"]].translation.x, poses[b["name"]].translation.y, poses[b["name"]].translation.z)), b["translation"]) for b in expected)
translation_factor = 1.0
if has_root_units and translation_error(1.0) > 0.001 and translation_error(100.0) <= 0.001:
    translation_factor = 100.0
corrected_names, corrected_poses = [], []
for bone in expected:
    name = bone["name"]
    parent = str(modifier.get_parent_name(name))
    expected_parent = expected[bone["parent"]]["name"] if bone["parent"] >= 0 else "None"
    if parent != expected_parent:
        raise RuntimeError("Native bone parent mismatch at " + name)
    transform = poses[name]
    t, q, s = transform.translation, transform.rotation, transform.scale3d
    translation = math.dist(tuple(v * translation_factor for v in (t.x, t.y, t.z)), bone["translation"])
    actual_q = (q.x, q.y, q.z, q.w)
    expected_q = bone["rotation"]
    dot = sum(a * b for a, b in zip(actual_q, expected_q))
    rotation = max(0.0, 1.0 - abs(dot))
    scales = (s.x, s.y, s.z)
    difference = max(abs(a - b) for a, b in zip(scales, bone["scale"]))
    finite = all(math.isfinite(v) for v in (*actual_q, *scales, translation, rotation))
    root_units = bone["parent"] == -1 and all(abs(b - 1.0) < 0.00001 for b in bone["scale"]) and all(abs(a - 100.0) < 0.0001 for a in scales)
    if not finite or translation > 0.001 or rotation > 0.00001 or (difference > 0.00001 and not root_units):
        raise RuntimeError("Native rest-pose mismatch at %s: translation %.6g cm, rotation metric %.6g, scale difference %.6g. Preserve the donor rest pose." % (name, translation, rotation, difference))
    if root_units:
        root_correction = name
    corrected = unreal.Transform()
    corrected.translation = unreal.Vector(*bone["translation"])
    corrected.rotation = unreal.Quat(*bone["rotation"])
    corrected.scale3d = unreal.Vector(*bone["scale"])
    corrected_names.append(name)
    corrected_poses.append(corrected)
if root_correction:
    # Bake the confirmed unit representation into native local translations and
    # remove its root scale together. Rebuild inverse binds from that hierarchy.
    if not modifier.set_bones_transforms(corrected_names, corrected_poses, True) or not modifier.commit_skeleton_to_skeletal_mesh():
        raise RuntimeError("Could not normalize the FBX root units and rebuild the bind matrices.")
    unreal.log("Batcomputer: normalized x100 FBX root units; kept mesh vertices, weights and native child local poses.")
materials = []
slots = []
for index, existing in enumerate(mesh.materials):
    name = str(existing.material_slot_name)
    slots.append(name)
    material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        "MI_Slot_" + str(index), task.destination_path, unreal.MaterialInstanceConstant,
        unreal.MaterialInstanceConstantFactoryNew())
    unreal.MaterialEditingLibrary.set_material_instance_parent(
        material, unreal.load_asset("/Engine/EngineMaterials/DefaultMaterial"))
    unreal.EditorAssetLibrary.save_loaded_asset(material)
    materials.append(unreal.SkeletalMaterial(material_interface=material, material_slot_name=name))
mesh.set_editor_property("materials", materials)
unreal.EditorAssetLibrary.save_loaded_asset(mesh.get_editor_property("skeleton"))
unreal.EditorAssetLibrary.save_loaded_asset(mesh)
(root / "import-result.json").write_text(json.dumps({
    "mesh": mesh.get_path_name(), "skeleton": mesh.get_editor_property("skeleton").get_path_name(),
    "slots": slots, "root_unit_correction": root_correction is not None,
}), encoding="utf-8")
