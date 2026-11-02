# Предпросмотр наземных растений (трава grass_gen.py, поле и сорняки field_gen.py) — максимально близко к игре:
#  - расстановка как у GroundDressing + Nature Renderer: ячейки 2 м, в ячейке floor(ожидаемое) + 1 с вероятностью дробной
#    части, ожидаемое = Density записи × worldDensity 0.6 × площадь ячейки (× DSCALE), места в ячейке случайные,
#    поворот случайный;
#  - размер — как SizeVariation() в RotorWash.hlsl: smaller = шум 6 м · 0.6 + своё · 0.4, масштаб 1 − SV·smaller^1.5;
#  - дальность и затухание — nr.renderDistance и scaleFade.y из манифеста (вдали экземпляр уходит в землю);
#  - теней от растений нет (в игре у этих деталей тени выключены; кроме ассетов с castShadows), вид без тональной
#    кривой (Standard: у камеры дрона нет tonemapping), солнце и небо — примерно как дневной Enviro;
#  - изнанка освещена той же нормалью, что и лицо (как в Unity): + просвечивание с обратной нормалью.
# Чего нет: мипов атласа (Cycles сглаживает выборками), ветра, сезонной перекраски шейдера.
# Запуск: blender.exe -b --factory-startup --python preview_ground.py -- <out.png> <высота камеры> <смесь>
#   смесь: 'имя:Density,имя:Density' — имя = начало имени папки ассета (полное имя — без примеси старых ассетов)
#   окружение: SRC (папки через ';'), GROUND (#rrggbb почвы), PITCH (вниз от горизонта, °), LENS (мм), SEED, SAMPLES,
#   DSCALE (множитель плотности), CELL (м), AREA ('x,y' — полуширина и дальность засева, м), SUN (высота солнца, °),
#   RES ('960x540'), SINGLE=1 (по одному экземпляру каждого имени в ряд, камера на CAMD м, взгляд на высоту TZ)
import json
import math
import os
import random
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

args = eg.script_args()
out_png, cam_h, mix = args[0], float(args[1]), args[2]
env = os.environ
srcs = [s for s in env.get('SRC', '').split(';') if s]
ground_hex = env.get('GROUND', '#7a6548')
pitch = float(env.get('PITCH', 12 if cam_h < 2 else 35))
lens = float(env.get('LENS', 18))
single = env.get('SINGLE') == '1'
area_x, area_y = (float(v) for v in env.get('AREA', '14,40').split(','))
sun_elev = float(env.get('SUN', 42))
cell = float(env.get('CELL', 2.0))
dscale = float(env.get('DSCALE', 1.0))
res_x, res_y = (int(v) for v in env.get('RES', '960x540').split('x'))
WORLD_DENSITY = 0.6

sc = eg.reset_scene()
materials = {}


def atlas_material(folder):
    atlas = next((os.path.join(folder, f) for f in os.listdir(folder) if f.endswith('_albedo.png')), None)
    if atlas in materials:
        return materials[atlas]
    mat = bpy.data.materials.new('m_' + os.path.basename(folder))
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes['Principled BSDF']
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(atlas)
    tex.interpolation = 'Cubic'
    nt.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value = 0.85
    attr = nt.nodes.new('ShaderNodeAttribute')
    attr.attribute_name = 'nrm'
    attr.attribute_type = 'GEOMETRY'
    vt = nt.nodes.new('ShaderNodeVectorTransform')
    vt.vector_type = 'NORMAL'
    vt.convert_from = 'OBJECT'
    vt.convert_to = 'WORLD'
    nt.links.new(attr.outputs['Vector'], vt.inputs['Vector'])
    nt.links.new(vt.outputs['Vector'], bsdf.inputs['Normal'])
    # Cycles не освещает грань светом с другой стороны геометрической нормали; в Unity обе стороны освещены по той же
    # нормали. Поэтому + просвечивание с обратной нормалью: из двух замыканий свет солнца принимает ровно одно
    out = nt.nodes['Material Output']
    neg = nt.nodes.new('ShaderNodeVectorMath')
    neg.operation = 'SCALE'
    neg.inputs['Scale'].default_value = -1.0
    nt.links.new(vt.outputs['Vector'], neg.inputs[0])
    back = nt.nodes.new('ShaderNodeBsdfTranslucent')
    nt.links.new(tex.outputs['Color'], back.inputs['Color'])
    nt.links.new(neg.outputs['Vector'], back.inputs['Normal'])
    add = nt.nodes.new('ShaderNodeAddShader')
    nt.links.new(bsdf.outputs['BSDF'], add.inputs[0])
    nt.links.new(back.outputs['BSDF'], add.inputs[1])
    nt.links.new(add.outputs['Shader'], out.inputs['Surface'])
    materials[atlas] = mat
    return mat


def ground_material():
    m = bpy.data.materials.new('ground')
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes['Principled BSDF']
    base = Vector(eg.hex_rgb(ground_hex))
    coarse = nt.nodes.new('ShaderNodeTexNoise')
    coarse.inputs['Scale'].default_value = 1.2
    coarse.inputs['Detail'].default_value = 6
    ramp = nt.nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = 0.3
    ramp.color_ramp.elements[0].color = (*(base * 0.8), 1)
    ramp.color_ramp.elements[1].position = 0.7
    ramp.color_ramp.elements[1].color = (*(base * 1.15), 1)
    nt.links.new(coarse.outputs['Fac'], ramp.inputs['Fac'])
    fine = nt.nodes.new('ShaderNodeTexNoise')
    fine.inputs['Scale'].default_value = 60
    fine.inputs['Detail'].default_value = 4
    mul = nt.nodes.new('ShaderNodeMix')
    mul.data_type = 'RGBA'
    mul.blend_type = 'MULTIPLY'
    mul.inputs['Factor'].default_value = 0.3
    nt.links.new(ramp.outputs['Color'], mul.inputs['A'])
    nt.links.new(fine.outputs['Color'], mul.inputs['B'])
    nt.links.new(mul.outputs['Result'], bsdf.inputs['Base Color'])
    bump = nt.nodes.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = 0.25
    bump.inputs['Distance'].default_value = 0.01
    nt.links.new(fine.outputs['Fac'], bump.inputs['Height'])
    nt.links.new(bump.outputs['Normal'], bsdf.inputs['Normal'])
    bsdf.inputs['Roughness'].default_value = 0.95
    return m


bpy.ops.mesh.primitive_plane_add(size=600)
bpy.context.active_object.data.materials.append(ground_material())


def find_assets(prefix):
    found = []
    for src in srcs:
        for dp, dn, fn in os.walk(src):
            if 'manifest.json' not in fn or not os.path.basename(dp).startswith(prefix):
                continue
            with open(os.path.join(dp, 'manifest.json'), encoding='utf-8') as mf:
                manifest = json.load(mf)
            found.append((dp, manifest))
    return sorted(found)


entries = []
for part in mix.split(','):
    prefix, density = part.split(':')
    objs = []
    for folder, manifest in find_assets(prefix):
        fbx = os.path.join(folder, manifest['lods'][0]['fbx'])
        bpy.ops.import_scene.fbx(filepath=fbx)
        o = bpy.context.selected_objects[0]
        o.data.materials.clear()
        o.data.materials.append(atlas_material(folder))
        o.location = (0, 0, -100)
        me = o.data
        a = me.attributes.new('nrm', 'FLOAT_VECTOR', 'CORNER')
        for i, c in enumerate(me.corner_normals):
            a.data[i].vector = c.vector
        mat = manifest['materials'][0]
        nr = manifest.get('nr') or {}
        objs.append(dict(obj=o, sv=max(0.0, mat.get('sizeVariation', 0.0)), dist=nr.get('renderDistance', 0.0) or 150.0,
                         fade=(mat.get('scaleFade') or [0, 0])[1], shadows=bool(manifest['lods'][0].get('castShadows')) and nr.get('shadows', True)))
        print('PREVIEW_ASSET', os.path.basename(folder))
    if not objs:
        print('PREVIEW_NO_FBX', prefix)
    entries.append((prefix, objs, float(density)))

# шум ~6 м для SizeVariation (как PuddleNoise: значения в узлах сетки, сглаженная интерполяция)
noise_rng = random.Random(91)
noise_grid = [[noise_rng.random() for _ in range(128)] for _ in range(128)]


def value_noise(x, y):
    ix, iy = math.floor(x), math.floor(y)
    fx, fy = x - ix, y - iy
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    g = noise_grid
    a, b = g[ix % 128][iy % 128], g[(ix + 1) % 128][iy % 128]
    c, d = g[ix % 128][(iy + 1) % 128], g[(ix + 1) % 128][(iy + 1) % 128]
    return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy


cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
sc.collection.objects.link(cam)
sc.camera = cam
cam.data.lens = lens
cam.data.clip_start = 0.02
if single:
    camd = float(env.get('CAMD', 1.0))
    target = Vector((0, 0, float(env.get('TZ', 0.15))))
    cam.location = target + Vector((0, -camd * math.cos(math.radians(pitch)), camd * math.sin(math.radians(pitch))))
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
else:
    cam.location = (0, -1.5, cam_h)
    cam.rotation_euler = (math.radians(90 - pitch), 0, 0)
cam_pos = Vector(cam.location)

rng = random.Random(int(env.get('SEED', 3)))
placed = 0


def put(item, x, y, yaw, scale):
    global placed
    o = item['obj'].copy()
    sc.collection.objects.link(o)
    o.location = (x, y, 0)
    o.rotation_euler = (math.radians(90), 0, yaw)  # FBX: Y вверх -> Z вверх
    o.scale = (scale, scale, scale)
    o.visible_shadow = item['shadows']
    placed += 1


if single:
    x = -(len(entries) - 1) * 0.45
    for prefix, objs, density in entries:
        for k, item in enumerate(objs[:max(1, int(density))]):
            put(item, x, k * 0.8, rng.uniform(0, math.tau), 1.0)
        x += 0.9
else:
    nx0, nx1 = int(math.floor(-area_x / cell)), int(math.ceil(area_x / cell))
    ny0, ny1 = int(math.floor(-6 / cell)), int(math.ceil(area_y / cell))
    for ix in range(nx0, nx1):
        for iy in range(ny0, ny1):
            for prefix, objs, density in entries:
                if not objs:
                    continue
                expected = density * WORLD_DENSITY * cell * cell * dscale
                n = int(expected) + (1 if rng.random() < expected - int(expected) else 0)
                for _ in range(n):
                    item = rng.choice(objs)
                    x, y = (ix + rng.random()) * cell, (iy + rng.random()) * cell
                    d = (Vector((x, y, 0)) - cam_pos).length
                    if d > item['dist']:
                        continue
                    smaller = min(1.0, max(0.0, value_noise(x / 6 + 91.7, y / 6 + 91.7) * 0.6 + rng.random() * 0.4))
                    s = 1.0 - item['sv'] * smaller * math.sqrt(smaller)
                    if item['fade'] > 0:
                        s *= min(1.0, max(0.0, (item['dist'] - d) / item['fade']))
                    if s <= 0.02:
                        continue
                    put(item, x, y, rng.uniform(0, math.tau), s)

sun = bpy.data.lights.new('sun', 'SUN')
sun.energy = float(env.get('SUN_E', 2.6))
sun.angle = math.radians(1.5)
so = bpy.data.objects.new('sun', sun)
sc.collection.objects.link(so)
so.rotation_euler = (math.radians(90 - sun_elev), 0, math.radians(float(env.get('SUN_AZ', 35))))
world = bpy.data.worlds.new('w')
sc.world = world
world.use_nodes = True
world.node_tree.nodes['Background'].inputs['Color'].default_value = (0.55, 0.65, 0.8, 1)
world.node_tree.nodes['Background'].inputs['Strength'].default_value = float(env.get('SKY', 0.7))
sc.render.engine = 'CYCLES'
sc.cycles.samples = int(env.get('SAMPLES', 48))
sc.cycles.device = 'CPU'
sc.cycles.use_denoising = True
sc.render.resolution_x = res_x
sc.render.resolution_y = res_y
sc.view_settings.view_transform = 'Standard'
sc.view_settings.look = 'None'
sc.render.filepath = out_png
bpy.ops.render.render(write_still=True)
print('PREVIEW_OK', out_png, 'instances', placed)
