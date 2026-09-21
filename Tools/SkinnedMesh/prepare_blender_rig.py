"""Prepare a Batcomputer native-reference GLB for a weighted FBX export in Blender.

Run once after importing Batcomputer's GLB reference and before exporting the final
mesh. Select the imported armature, then run this script from Blender's Scripting
workspace. It changes only the armature's rest-space conversion and object name;
it never adds, removes, or renames a game bone.
"""
import bpy
from math import pi
from mathutils import Matrix


armature = bpy.context.active_object
if armature is None or armature.type != "ARMATURE":
    armature = next((obj for obj in bpy.context.selected_objects if obj.type == "ARMATURE"), None)
if armature is None:
    raise RuntimeError("Select the imported Batcomputer armature, then run this script once.")
if armature.get("batcomputer_fbx_basis_v1"):
    raise RuntimeError("This armature has already been prepared. Do not run the helper twice.")
if bpy.context.mode != "OBJECT":
    bpy.ops.object.mode_set(mode="OBJECT")

bones = armature.data.bones
if not bones.get("Root"):
    raise RuntimeError("The selected armature has no native Root bone.")

local_by_name = {
    bone.name: bone.matrix_local.copy() if bone.parent is None
    else bone.parent.matrix_local.inverted_safe() @ bone.matrix_local
    for bone in bones
}
parent_by_name = {bone.name: bone.parent.name if bone.parent else None for bone in bones}
conversion = Matrix.Rotation(pi / 2, 4, "X")
inverse_conversion = conversion.inverted_safe()
targets = {}
pending = set(local_by_name)
while pending:
    for name in list(pending):
        parent = parent_by_name[name]
        if parent is not None and parent not in targets:
            continue
        local = Matrix.Identity(4) if parent is None else conversion @ local_by_name[name] @ inverse_conversion
        targets[name] = local if parent is None else targets[parent] @ local
        pending.remove(name)

bpy.context.view_layer.objects.active = armature
bpy.ops.object.mode_set(mode="EDIT")
for name, matrix in targets.items():
    armature.data.edit_bones[name].matrix = matrix
bpy.ops.object.mode_set(mode="OBJECT")
armature.name = "Armature"
armature["batcomputer_fbx_basis_v1"] = True
# The native Root is deliberately zero-length at the feet, so its Blender marker
# looks tiny beside a minifig. Make the complete hierarchy visible without
# touching any transforms, geometry, or weights.
armature.show_in_front = True
armature.data.display_type = "STICK"
armature.data.show_names = True

print("Batcomputer: prepared %d native bones for FBX export. The small Root marker at the feet is normal; use the visible stick hierarchy as the scale reference. Select only the mesh and Armature; export binary FBX with Only Deform Bones on, Add Leaf Bones off, Armature FBXNode Type Null, Bake Animation off, and Apply Modifiers off." % len(bones))
