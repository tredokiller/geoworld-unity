# Предпросмотр травы: куртины из FBX на земле, солнце, камера с высоты дрона. Только для проверки на глаз.
import math
import os
import random
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

args = eg.script_args()
src, out_png = args[0], args[1]
cam_h = float(args[2]) if len(args) > 2 else 1.0
mix = args[3] if len(args) > 3 else 'short_sward:1.0'
sc = eg.reset_scene()
atlas = next(os.path.join(dp, f) for dp, dn, fn in os.walk(src) for f in fn if f.endswith('_atlas_albedo.png'))
albedo = bpy.data.images.load(atlas)
mat = bpy.data.materials.new('grass'); mat.use_nodes = True
nt = mat.node_tree; bsdf = nt.nodes['Principled BSDF']
tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = albedo
nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
bsdf.inputs['Roughness'].default_value = 0.85
bsdf.inputs['Subsurface Weight'].default_value = 0.0
# нормаль — из атрибута (Cycles переворачивает нормаль у изнанки полигона, в Unity изнанка берёт ту же нормаль)
nrm_attr = nt.nodes.new('ShaderNodeAttribute'); nrm_attr.attribute_name = 'nrm'; nrm_attr.attribute_type = 'GEOMETRY'
nrm_vt = nt.nodes.new('ShaderNodeVectorTransform'); nrm_vt.vector_type = 'NORMAL'; nrm_vt.convert_from = 'OBJECT'; nrm_vt.convert_to = 'WORLD'
nt.links.new(nrm_attr.outputs['Vector'], nrm_vt.inputs['Vector']); nt.links.new(nrm_vt.outputs['Vector'], bsdf.inputs['Normal'])
ground = bpy.data.materials.new('ground'); ground.use_nodes = True
ground.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (*eg.hex_rgb(os.environ.get('GROUND', '#4a3f2c')), 1)
bpy.ops.mesh.primitive_plane_add(size=40); bpy.context.active_object.data.materials.append(ground)
patches = {}
for part in mix.split(','):
    name, share = part.split(':')
    files = [os.path.join(dp, f) for dp, dn, fn in os.walk(src) for f in fn if f.endswith('.fbx') and (f.startswith(name) or f.startswith('grass_' + name))]
    objs = []
    for f in files:
        bpy.ops.import_scene.fbx(filepath=f)
        o = bpy.context.selected_objects[0]; o.data.materials.clear(); o.data.materials.append(mat); o.location = (0, 0, -100)
        me = o.data
        a = me.attributes.new('nrm', 'FLOAT_VECTOR', 'CORNER')
        for i, c in enumerate(me.corner_normals): a.data[i].vector = c.vector
        objs.append(o)
    patches[name] = (objs, float(share))
rng = random.Random(3)
spacing = 0.55
for ix in range(-14, 15):
    for iy in range(-4, 40):
        for name, (objs, share) in patches.items():
            if rng.random() > share: continue
            o = rng.choice(objs).copy(); sc.collection.objects.link(o)
            o.location = (ix * spacing + rng.uniform(-0.25, 0.25), iy * spacing + rng.uniform(-0.25, 0.25), 0)
            o.rotation_euler = (math.radians(90), 0, rng.uniform(0, math.tau))  # FBX: Y вверх -> назад в Z вверх
            s = rng.uniform(0.8, 1.2); o.scale = (s, s, s)
sun = bpy.data.lights.new('sun', 'SUN'); sun.energy = 3.5; sun.angle = math.radians(1.5)
so = bpy.data.objects.new('sun', sun); sc.collection.objects.link(so); so.rotation_euler = (math.radians(50), 0, math.radians(35))
world = bpy.data.worlds.new('w'); sc.world = world; world.use_nodes = True
world.node_tree.nodes['Background'].inputs['Color'].default_value = (0.55, 0.65, 0.8, 1); world.node_tree.nodes['Background'].inputs['Strength'].default_value = 1.0
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); sc.collection.objects.link(cam); sc.camera = cam
cam.data.lens = 18
cam.location = (0, -1.5, cam_h); cam.rotation_euler = (math.radians(90 - 12 if cam_h < 3 else 90 - 35), 0, 0)
sc.render.engine = 'CYCLES'; sc.cycles.samples = 48; sc.cycles.device = 'CPU'; sc.cycles.use_denoising = True
sc.render.resolution_x = 960; sc.render.resolution_y = 540
sc.view_settings.view_transform = 'AgX'
sc.render.filepath = out_png
bpy.ops.render.render(write_still=True)
print('PREVIEW_OK', out_png)
