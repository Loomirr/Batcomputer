"""Blender CLI: --background --factory-startup --python this.py -- source.fbx output-dir.
Builds an offline glTF asset and editable Blender copy, leaving the FBX/texture unchanged.
"""
import bpy, sys, os, math
from mathutils import Vector, Matrix
source, output = sys.argv[sys.argv.index('--') + 1:]
source, output = os.path.abspath(source), os.path.abspath(output)
os.makedirs(output, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.abspath(source))
parts = list(bpy.context.scene.objects)
required = ['Batcomputer','BatSymbol','LeverBase','LeverHandle']
assert all(n in bpy.data.objects for n in required), 'Expected original four-part Batcomputer model'
for obj in parts:
    if obj.type == 'MESH':
        obj.data.transform(obj.matrix_world);obj.matrix_world=Matrix.Identity(4)
        for v in obj.data.vertices: v.co *= .055
for image in bpy.data.images:
    if image.source == 'FILE':
        candidate=os.path.join(os.path.dirname(source),os.path.basename(bpy.path.abspath(image.filepath).replace('\\','/')))
        if os.path.isfile(candidate):image.filepath=candidate;image.reload()
        assert image.size[0]>0, 'Missing model texture: '+image.filepath
        image.pack()
for mat in bpy.data.materials:
    if not mat.use_nodes:continue
    bsdf=next((n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED'),None)
    if bsdf:
        bsdf.inputs['Roughness'].default_value=.38
        bsdf.inputs['Metallic'].default_value=.06
        if 'Coat Weight' in bsdf.inputs:bsdf.inputs['Coat Weight'].default_value=.2
    for n in list(mat.node_tree.nodes):
        if n.type=='NORMAL_MAP' and not n.inputs['Color'].is_linked:mat.node_tree.nodes.remove(n)
for p in bpy.data.objects['LeverHandle'].data.polygons:p.use_smooth=True
# Small manufactured edge radii catch the studio reflections instead of razor-sharp CAD edges.
# Keep the source's silhouette, UVs, print and four named parts intact.
for name in required:
    obj=bpy.data.objects[name]
    bpy.context.view_layer.objects.active=obj
    bevel=obj.modifiers.new('Molded edge highlights','BEVEL');bevel.width=.018 if name=='Batcomputer' else .009
    bevel.segments=3;bevel.limit_method='ANGLE';bevel.angle_limit=math.radians(35)
    bpy.ops.object.modifier_apply(modifier=bevel.name)
    if name!='LeverHandle':
        normals=obj.modifiers.new('Weighted corner normals','WEIGHTED_NORMAL');normals.keep_sharp=True;normals.weight=50
        bpy.ops.object.modifier_apply(modifier=normals.name)
    # Separate material instances permit a satin chassis, glossy printed tile and metal lever.
    for slot in obj.material_slots:
        if not slot.material:continue
        slot.material=slot.material.copy();slot.material.name='Studio_'+name
        bsdf=next((n for n in slot.material.node_tree.nodes if n.type=='BSDF_PRINCIPLED'),None)
        if not bsdf:continue
        bsdf.inputs['Roughness'].default_value={'Batcomputer':.32,'BatSymbol':.2,'LeverBase':.28,'LeverHandle':.22}[name]
        bsdf.inputs['Metallic'].default_value=.65 if name=='LeverHandle' else 0
        if 'Coat Weight' in bsdf.inputs:bsdf.inputs['Coat Weight'].default_value=.45 if name=='BatSymbol' else .25
        if 'Coat Roughness' in bsdf.inputs:bsdf.inputs['Coat Roughness'].default_value=.18
# Turn the printed keypad into shallow molded caps, aligned through the original UVs.
console=bpy.data.objects['Batcomputer'];console.data.calc_loop_triangles()
def panel_point(px,py):
    uv=Vector((px/1024,1-py/1024));layer=console.data.uv_layers.active.data
    for tri in console.data.loop_triangles:
        a,b,c=[layer[i].uv.copy() for i in tri.loops];ab=b-a;ac=c-a;d=uv-a
        det=ab.x*ac.y-ab.y*ac.x
        if abs(det)<1e-9:continue
        u=(d.x*ac.y-d.y*ac.x)/det;v=(ab.x*d.y-ab.y*d.x)/det
        if u>=-1e-5 and v>=-1e-5 and u+v<=1.00001:
            va,vb,vc=[console.data.vertices[i].co for i in tri.vertices]
            return va*(1-u-v)+vb*u+vc*v,tri.normal.copy()
    raise RuntimeError('No console surface at UV '+str((px,py)))
for row,py in enumerate([567,633,710,786,869,948]):
    for col,px in enumerate([586,695,804]):
        center,normal=panel_point(px,py);pu,_=panel_point(px+24,py);pv,_=panel_point(px,py-16)
        u=pu-center;v=pv-center;v=normal.cross(u.normalized())*v.length
        bpy.ops.mesh.primitive_cube_add(size=1,location=center+normal*.014);key=bpy.context.object;key.name=f'Keycap_{row}_{col}'
        basis=Matrix((u.normalized(),v.normalized(),normal)).transposed();key.rotation_euler=basis.to_euler();key.dimensions=(u.length*2,v.length*2,.028)
        bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
        bevel=key.modifiers.new('Rounded key edge','BEVEL');bevel.width=.009;bevel.segments=3;bpy.ops.object.modifier_apply(modifier=bevel.name)
        colors=[[(.28,.32,.38)]*3,[(.28,.32,.38)]*3,[(.28,.32,.38)]*3,[(.48,.015,.018)]*3,[(.025,.46,.06),(.65,.25,.015),(.02,.08,.65)],[(.025,.46,.06),(.48,.015,.018),(.025,.46,.06)]]
        color=colors[row][col];mat=bpy.data.materials.new('Indicator_'+key.name if row>=3 else 'Key_'+key.name);mat.use_nodes=True
        bsdf=mat.node_tree.nodes.get('Principled BSDF');bsdf.inputs['Base Color'].default_value=(*color,1);bsdf.inputs['Roughness'].default_value=.24
        if 'Coat Weight' in bsdf.inputs:bsdf.inputs['Coat Weight'].default_value=.5
        if row>=3:bsdf.inputs['Emission Color'].default_value=(*color,1);bsdf.inputs['Emission Strength'].default_value=.18
        key.data.materials.append(mat);parts.append(key)
root=bpy.data.objects.new('BatcomputerRoot',None);bpy.context.collection.objects.link(root)
for o in parts:o.parent=root
pivot=bpy.data.objects.new('LeverPivot',None);bpy.context.collection.objects.link(pivot)
pivot.parent=root;pivot.location=Vector((-9.811614,-10,34))*.055
handle=bpy.data.objects['LeverHandle'];world=handle.matrix_world.copy();handle.parent=pivot
bpy.context.view_layer.update();handle.matrix_world=world
for frame,degrees in [(1,0),(31,-18),(61,0),(91,18),(121,0)]:
    pivot.rotation_euler.x=math.radians(degrees);pivot.keyframe_insert(data_path='rotation_euler',frame=frame)
pivot.animation_data.action.name='LeverCycle'
scene=bpy.context.scene;scene.frame_start=1;scene.frame_end=121;scene.render.fps=30;scene.frame_set(1)
bpy.ops.object.select_all(action='DESELECT')
for o in parts+[root,pivot]:o.select_set(True)
bpy.ops.export_scene.gltf(filepath=os.path.join(output,'Batcomputer.glb'),export_format='GLB',use_selection=True,export_animations=True,export_animation_mode='ACTIONS',export_force_sampling=True,export_yup=True)
# A studio setup is included only in the editable .blend, never in the shipped GLB.
scene.world=bpy.data.worlds.new('Studio');scene.world.color=(.16,.18,.22)
def aim(o,target):o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler()
bpy.ops.object.camera_add(location=(5.5,8,6));camera=bpy.context.object;aim(camera,(0,0,1.9));camera.data.type='ORTHO';camera.data.ortho_scale=5.9;scene.camera=camera
for pos,power,size in [((3,4,7),850,5),((-4,2,5),600,4),((1,-4,6),1000,4)]:
    bpy.ops.object.light_add(type='AREA',location=pos);lamp=bpy.context.object;lamp.data.energy=power;lamp.data.shape='DISK';lamp.data.size=size;aim(lamp,(0,0,1.5))
scene.render.engine='CYCLES';scene.cycles.samples=32
scene.render.resolution_x=640;scene.render.resolution_y=560;scene.render.resolution_percentage=100
scene.render.film_transparent=True
scene.render.filepath=os.path.join(output,'Batcomputer-poster.png');bpy.ops.render.render(write_still=True)
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_distance=7
            area.spaces.active.region_3d.view_location=Vector((0,0,1.9))
            area.spaces.active.region_3d.view_rotation=camera.rotation_euler.to_quaternion()
            area.spaces.active.shading.type='MATERIAL'
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(output,'Batcomputer-Updater.blend'))
print('PREPARED: original console plus beveled keycaps, studio materials, packed texture and LeverCycle animation')
