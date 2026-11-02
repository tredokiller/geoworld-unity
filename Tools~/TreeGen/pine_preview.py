# Предпросмотр сосен из pine_gen.py (FBX из папок ассетов, как их получит Unity): Cycles, солнце + небо, земля бора
# (песок, хвойный опад, мох). Только для проверки на глаз.
#   blender.exe -b --factory-startup --python pine_preview.py -- <папка ассетов> <папка PNG> <режим> [имя ...]
# режимы: close — 3 м от ствола на высоте 1.5 м, взгляд вверх в крону; bark — кора у комля и переход коры;
#   crown — крона изнутри/снизу; medium — дерево целиком с 25 м с высоты 2 м (и против солнца);
#   lods — LOD0..LOD3 рядом в одном масштабе (сбоку и сверху под 40°);
#   stand60 / stand150 — куртина из 16-20 деревьев с высоты 60/150 м под 40° вниз (LOD по доле экрана, как в Unity;
#   с подлеском), кадр широкой камерой FPV и крупнее тем же выбором LOD.
# Хвоя — двусторонняя, нормали вершин на обеих сторонах (как в Unity); крест — карта нормалей, Cull Back.
import json
import math
import os
import random
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402
import pine_gen as pg  # noqa: E402

LOD_SCREEN = pg.LOD_SCREEN


def setup_render(scene, w, h, samples):
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.cycles.transparent_max_bounces = 64
    scene.cycles.max_bounces = 6
    pg.setup_gpu(scene)
    scene.render.resolution_x, scene.render.resolution_y = w, h
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0.0
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGB'


def setup_world(scene, sun_elev=48, sun_az=-60):
    world = bpy.data.worlds.new('sky')
    scene.world = world
    nt = world.node_tree
    bg = nt.nodes['Background']
    tc = nt.nodes.new('ShaderNodeTexCoord')
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    nt.links.new(tc.outputs['Generated'], sep.inputs[0])
    ramp = nt.nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = 0.5
    ramp.color_ramp.elements[0].color = (0.62, 0.68, 0.74, 1)
    ramp.color_ramp.elements[1].position = 0.85
    ramp.color_ramp.elements[1].color = (0.33, 0.45, 0.68, 1)
    nt.links.new(sep.outputs['Z'], ramp.inputs['Fac'])
    nt.links.new(ramp.outputs['Color'], bg.inputs['Color'])
    bg.inputs['Strength'].default_value = 0.55
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = 1.9
    sun.angle = math.radians(0.6)
    sun.color = (1.0, 0.96, 0.9)
    so = bpy.data.objects.new('sun', sun)
    eg.link(so)
    d = Vector((math.cos(math.radians(sun_az)) * math.cos(math.radians(sun_elev)),
                math.sin(math.radians(sun_az)) * math.cos(math.radians(sun_elev)), math.sin(math.radians(sun_elev))))
    so.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()


def ground_material():
    m = bpy.data.materials.new('ground')
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    bsdf = N['Principled BSDF']
    bsdf.inputs['Roughness'].default_value = 0.95
    tc = N.new('ShaderNodeTexCoord')
    n1 = N.new('ShaderNodeTexNoise')
    n1.inputs['Scale'].default_value = 0.08
    n1.inputs['Detail'].default_value = 6
    L.new(tc.outputs['Object'], n1.inputs['Vector'])
    n2 = N.new('ShaderNodeTexNoise')
    n2.inputs['Scale'].default_value = 1.5
    n2.inputs['Detail'].default_value = 8
    L.new(tc.outputs['Object'], n2.inputs['Vector'])
    ramp = N.new('ShaderNodeValToRGB')
    cr = ramp.color_ramp
    cr.elements[0].position = 0.3
    cr.elements[0].color = (*eg.hex_rgb('#5e4e3d'), 1)
    cr.elements[1].position = 0.78
    cr.elements[1].color = (*eg.hex_rgb('#b09a72'), 1)
    for pos, col in ((0.45, '#6e5c47'), (0.53, '#646439'), (0.62, '#83704f')):
        e = cr.elements.new(pos)
        e.color = (*eg.hex_rgb(col), 1)
    mixf = N.new('ShaderNodeMath')
    mixf.operation = 'MULTIPLY_ADD'
    mixf.inputs[1].default_value = 0.25
    L.new(n2.outputs['Fac'], mixf.inputs[0])
    L.new(n1.outputs['Fac'], mixf.inputs[2])
    L.new(mixf.outputs[0], ramp.inputs['Fac'])
    L.new(ramp.outputs['Color'], bsdf.inputs['Base Color'])
    return m


def add_ground(size=600):
    bpy.ops.mesh.primitive_plane_add(size=size, location=(0, 0, 0))
    g = bpy.context.active_object
    g.data.materials.append(ground_material())
    return g


def add_understory(rng, area, count):
    """Подлесок субора: молодые дубы/крушина 1.5-4 м — зелёные комья (только для ощущения масштаба и цвета)."""
    m = bpy.data.materials.new('understory')
    bsdf = m.node_tree.nodes['Principled BSDF']
    bsdf.inputs['Base Color'].default_value = (*eg.hex_rgb('#4f6b2c'), 1)
    bsdf.inputs['Roughness'].default_value = 0.9
    tc = m.node_tree.nodes.new('ShaderNodeTexNoise')
    tc.inputs['Scale'].default_value = 3.0
    bump = m.node_tree.nodes.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = 0.8
    m.node_tree.links.new(tc.outputs['Fac'], bump.inputs['Height'])
    m.node_tree.links.new(bump.outputs['Normal'], bsdf.inputs['Normal'])
    for i in range(count):
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=1.0,
                                              location=(rng.uniform(-area, area), rng.uniform(-area, area), 0))
        o = bpy.context.active_object
        s = rng.uniform(0.8, 1.8)
        o.scale = (s * rng.uniform(0.8, 1.2), s * rng.uniform(0.8, 1.2), s * rng.uniform(0.7, 1.1))
        o.location.z = s * 0.7
        o.data.materials.append(m)


def load_asset(root, name):
    """LOD0..3 из FBX; материалы — превью-материалы pine_gen с текстурами из папки ассета."""
    folder = os.path.join(root, name)
    with open(os.path.join(folder, 'manifest.json')) as f:
        man = json.load(f)
    mats = {}
    for m in man['materials']:
        alb = os.path.join(folder, m['albedo'])
        nrm = os.path.join(folder, m['normal'])
        if m['role'] == 'bark':
            mats[m['id']] = pg.bark_material(name + '_' + m['id'], alb, nrm)
        elif m['role'] == 'cross':
            mats[m['id']] = pg.card_material(name + '_' + m['id'], alb, m.get('alphaCutoff', 0.5), normal=nrm,
                                             cull_back=True)
        else:
            mats[m['id']] = pg.card_material(name + '_' + m['id'], alb, m.get('alphaCutoff', 0.5))
    lods = []
    for i, lod in enumerate(man['lods']):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=os.path.join(folder, lod['fbx']))
        new = [o for o in bpy.data.objects if o not in before and o.type == 'MESH']
        o = new[0]
        assert len(o.data.materials) == len(lod['submeshes']), (len(o.data.materials), lod['submeshes'])
        for j, mid in enumerate(lod['submeshes']):
            o.data.materials[j] = mats[mid]
        bpy.ops.object.select_all(action='DESELECT')
        o.select_set(True)
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
        pg.add_nrm_attr(o)
        o.location = (0, 0, -1000)
        o['lod'] = i
        bpy.context.view_layer.update()
        lods.append(o)
    return man, lods


def place_instance(src, loc, yaw, scale, shadows=True):
    o = src.copy()
    eg.link(o)
    o.location = loc
    o.rotation_euler = (0, 0, yaw)
    o.scale = (scale, scale, scale)
    o.visible_shadow = shadows
    return o


def camera(scene, loc, target, lens):
    for ob in [x for x in bpy.data.objects if x.name.startswith('cam')]:
        bpy.data.objects.remove(ob)
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    eg.link(cam)
    cam.data.lens = lens
    cam.data.clip_end = 5000
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    scene.camera = cam
    return cam


def pick_lod(cam, scene, pos, height):
    """Доля высоты экрана, как у LODGroup Unity (вертикальный угол камеры)."""
    w, h = scene.render.resolution_x, scene.render.resolution_y
    sensor = cam.data.sensor_width
    fov_h = 2 * math.atan(sensor / 2 / cam.data.lens)
    fov_v = 2 * math.atan(math.tan(fov_h / 2) * h / w)
    d = (Vector(pos) + Vector((0, 0, height * 0.5)) - cam.location).length
    s = height / (2 * d * math.tan(fov_v / 2))
    for i, th in enumerate(LOD_SCREEN):
        if s >= th:
            return i, s
    return 3, s


def render(scene, path):
    bpy.context.view_layer.update()
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print('PREVIEW', path)


def main():
    args = eg.script_args()
    root, out_dir, mode = args[0], args[1], args[2]
    names = args[3:] or list(pg.PRESETS)
    os.makedirs(out_dir, exist_ok=True)
    scene = eg.reset_scene()
    setup_world(scene)
    assets = {n: load_asset(root, n) for n in names}
    tag = '_'.join(n.replace('pine_', '') for n in names) if len(names) <= 2 else 'mix'
    if mode in ('close', 'medium', 'bark', 'crown'):
        add_ground()
        man, lods = assets[names[0]]
        H = man['height']
        place_instance(lods[0], (0, 0, 0), 0.0, 1.0)
        P = pg.PRESETS[names[0]]
        if mode == 'close':
            setup_render(scene, 1280, 960, 128)
            camera(scene, (0.0, -3.0, 1.5), (0.0, 0.6, H * 0.62), 14)
            render(scene, os.path.join(out_dir, f'{tag}_close.png'))
            camera(scene, (2.2, -2.0, 1.5), (-0.3, 0.4, H * 0.8), 24)
            render(scene, os.path.join(out_dir, f'{tag}_close_up.png'))
        elif mode == 'medium':
            setup_render(scene, 960, 1280, 96)
            camera(scene, (0.0, -25.0, 2.0), (0.0, 0.0, H * 0.5), 24)
            render(scene, os.path.join(out_dir, f'{tag}_medium.png'))
            camera(scene, (0.0, 25.0, 2.0), (0.0, 0.0, H * 0.5), 24)
            render(scene, os.path.join(out_dir, f'{tag}_medium_back.png'))
        elif mode == 'bark':
            rt = P['rough_top']
            setup_render(scene, 1280, 960, 96)
            camera(scene, (0.35, -1.25, 1.4), (0.0, 0.0, 1.3), 30)
            render(scene, os.path.join(out_dir, f'{tag}_bark.png'))
            camera(scene, (0.0, -3.0, 1.6), (0.0, 0.0, rt + 0.4), 40)
            render(scene, os.path.join(out_dir, f'{tag}_bark_transition.png'))
            camera(scene, (0.0, -1.6, rt + 0.2), (0.0, 0.0, rt + 0.5), 35)
            render(scene, os.path.join(out_dir, f'{tag}_bark_transition_near.png'))
        else:
            zcb = P['crown_base'] * H
            setup_render(scene, 1280, 960, 96)
            camera(scene, (4.0, -5.0, zcb + 0.8), (0.0, 0.0, zcb + (H - zcb) * 0.45), 22)
            render(scene, os.path.join(out_dir, f'{tag}_crown.png'))
    elif mode == 'lods':
        add_ground()
        man, lods = assets[names[0]]
        H = man['height']
        gap = H * 0.42
        inst = [place_instance(src, ((i - 1.5) * gap, 0, 0), 0.0, 1.0) for i, src in enumerate(lods)]
        setup_render(scene, 1600, 900, 64)
        # как в игре вдали (Nature Renderer не рисует теней деревьев дальше 70 м): все уровни без теней
        for o in inst:
            o.visible_shadow = False
        camera(scene, (0.0, -H * 2.4, H * 0.45), (0.0, 0.0, H * 0.5), 35)
        render(scene, os.path.join(out_dir, f'{tag}_lods.png'))
        camera(scene, (0.0, -H * 1.9, H * 1.75), (0.0, 0.0, H * 0.55), 35)
        render(scene, os.path.join(out_dir, f'{tag}_lods_above.png'))
        for o in inst:
            o.visible_shadow = True
        camera(scene, (0.0, -H * 2.4, H * 0.45), (0.0, 0.0, H * 0.5), 35)
        render(scene, os.path.join(out_dir, f'{tag}_lods_shadow.png'))
    else:
        add_ground(2000)
        alt = {'stand60': 60.0, 'stand150': 150.0}[mode]
        rng = random.Random(5)
        mature = any('forest' in n for n in names)
        spacing = 4.6 if mature else 3.0
        cells = 5
        positions = []
        for ix in range(cells):
            for iy in range(cells):
                if rng.random() < 0.22:
                    continue
                positions.append(Vector(((ix - (cells - 1) / 2) * spacing + rng.uniform(-1.0, 1.0),
                                         (iy - (cells - 1) / 2) * spacing + rng.uniform(-1.0, 1.0), 0)))
        positions = positions[:20]
        setup_render(scene, 1280, 720, 64)
        look = math.radians(40)
        dist_h = alt / math.tan(look)
        cam = camera(scene, (0, -dist_h, alt), (0, 0, 10), 16)
        counts = [0, 0, 0, 0]
        for k, p in enumerate(positions):
            n = names[k % len(names)]
            man, lods = assets[n]
            sc = rng.uniform(0.85, 1.15)
            li, s = pick_lod(cam, scene, p, man['height'] * sc)
            counts[li] += 1
            place_instance(lods[li], p, rng.uniform(0, math.tau), sc)
        print('LODS', counts)
        render(scene, os.path.join(out_dir, f'{tag}_{mode}.png'))
        cam.data.lens = {'stand60': 40, 'stand150': 85}[mode]
        hmax = max(assets[n][0]['height'] for n in names)
        cam.rotation_euler = (Vector((0, 0, hmax * 0.5)) - cam.location).to_track_quat('-Z', 'Y').to_euler()
        render(scene, os.path.join(out_dir, f'{tag}_{mode}_zoom.png'))


if __name__ == '__main__':
    main()
