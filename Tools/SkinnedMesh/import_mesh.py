"""Headless UE 5.6 import. Input JSON is data, never Python code supplied by the FBX."""
import json
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
    "slots": slots,
}), encoding="utf-8")
