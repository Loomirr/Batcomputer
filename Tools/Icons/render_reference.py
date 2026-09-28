"""Render an exported viewer assembly in the supplied template, without saving the .blend.

blender --background --factory-startup --disable-autoexec template.blend
        --python render_reference.py -- assembly.glb output.png
"""
import bpy
import sys
from pathlib import Path

args = sys.argv[sys.argv.index('--') + 1:]
source, output = [str(Path(arg).resolve()) for arg in args]
scene = bpy.context.scene
# Preserve the reference camera, lights, world, compositor and colour management.
for obj in list(scene.objects):
    if obj.type == 'MESH':
        obj.hide_render = True
before = set(scene.objects)
bpy.ops.import_scene.gltf(filepath=source)
imported = set(scene.objects) - before
for obj in imported:
    if obj.parent not in imported:
        obj.scale *= 100  # glTF metres -> template's centimetre-sized authoring scene
scene.frame_set(2)
scene.render.filepath = output
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
bpy.ops.render.render(write_still=True)
