"""Read an icon template in background Blender without changing the source file."""
import bpy
import json
import sys
from pathlib import Path
from mathutils import Vector

out = Path(sys.argv[sys.argv.index('--') + 1])
out.parent.mkdir(parents=True, exist_ok=True)
scene = bpy.context.scene
depsgraph = bpy.context.evaluated_depsgraph_get()
def matrix(obj):
    return [list(row) for row in obj.matrix_world]
def simple(value):
    if isinstance(value, (str, bool, int, float)):
        return value
    try:
        return list(value)
    except TypeError:
        return str(value)
def props(value, names):
    return {name: simple(getattr(value, name)) for name in names if hasattr(value, name)}
objects = []
for obj in scene.objects:
    entry = dict(name=obj.name, type=obj.type, matrix=matrix(obj), hidden=obj.hide_render)
    entry['evaluatedMatrix'] = matrix(obj.evaluated_get(depsgraph))
    entry['visible'] = obj.visible_get()
    if obj.type == 'CAMERA':
        entry['camera'] = props(obj.data, ['type', 'lens', 'sensor_width', 'sensor_height', 'sensor_fit', 'ortho_scale', 'shift_x', 'shift_y', 'clip_start', 'clip_end'])
        entry['viewFrame'] = [list(v) for v in obj.data.view_frame(scene=scene)]
    if obj.type == 'LIGHT':
        entry['light'] = props(obj.data, ['type', 'energy', 'color', 'shape', 'size', 'size_y', 'angle', 'shadow_soft_size', 'normalize', 'use_shadow'])
    if obj.type == 'MESH':
        points = [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]
        entry['bounds'] = [[min(p[i] for p in points) for i in range(3)], [max(p[i] for p in points) for i in range(3)]]
        entry['vertices'] = len(obj.data.vertices)
        entry['materials'] = [slot.material.name if slot.material else None for slot in obj.material_slots]
    objects.append(entry)
world_nodes = []
if scene.world and scene.world.use_nodes:
    for node in scene.world.node_tree.nodes:
        world_nodes.append(dict(type=node.type, name=node.name, inputs={s.name:simple(s.default_value) for s in node.inputs if hasattr(s, 'default_value')}))
report = dict(blender=bpy.app.version_string, frame=scene.frame_current, camera=scene.camera.name if scene.camera else None,
    render=props(scene.render, ['engine','resolution_x','resolution_y','resolution_percentage','film_transparent','pixel_aspect_x','pixel_aspect_y']),
    units=props(scene.unit_settings, ['system','scale_length']),
    color=props(scene.view_settings, ['view_transform','look','exposure','gamma']),
    world=world_nodes, objects=objects)
out.write_text(json.dumps(report, indent=2), encoding='utf-8')
print('ICON_TEMPLATE_REPORT=' + str(out))
