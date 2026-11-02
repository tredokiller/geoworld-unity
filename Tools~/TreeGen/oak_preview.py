# Предпросмотр дубов (Cycles, фоновый Blender): импорт FBX из папок ассетов, материалы из их PNG, солнце + небо.
#   blender -b --factory-startup --python oak_preview.py -- <корень выдачи> <режим> <png> [ассеты...]
# Режимы:
#   close    — 3 м от ствола на высоте 1.5 м, взгляд вверх в крону (LOD0);
#   lens     — объектив дрона на взлёте: 0.10 м над землёй, 1 м от ствола, вверх на 25° (LOD0);
#   medium   — всё дерево с 25 м с высоты 2 м (LOD по доле экрана);
#   lineup   — все ассеты LOD0 в ряд;
#   lods     — LOD0..LOD3 рядом в одном масштабе + числа (покрытие и яркость кроны по LOD) в <png>.json;
#   stand<A> — роща 16 лесных дубов (шаг 9 ± 2 м) с высоты A м под 40° вниз, LOD — как выбрал бы Unity (FOV 80°);
#              в кадре — приближение (FOV 24°), второй кадр <png>_wide.png — весь FOV 80°;
#   field<A> — опушка: лес и отдельные раскидистые дубы в поле;
#   cross<A> — роща целиком на LOD3 (дальняя карточка) с высоты A м.
# Свет: солнце 3.5 (45°, азимут 135°), рассеянный свет неба #9FB4D0 x 0.7, вид Standard (в игре нет тонмаппинга);
# фон — небо Хосека-Вилки (только для камеры).
import json
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
from mathutils import Matrix, Vector  # noqa: E402

import envgen_common as eg  # noqa: E402

FOV_LOD = 80.0  # вертикальный угол камеры дрона: по нему Unity выбирает LOD


def use_gpu(scene):
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        for t in ('OPTIX', 'CUDA'):
            try:
                prefs.compute_device_type = t
                prefs.get_devices()
                if [d for d in prefs.devices if d.type == t]:
                    for d in prefs.devices:
                        d.use = d.type == t
                    scene.cycles.device = 'GPU'
                    return t
            except Exception:
                continue
    except Exception:
        pass
    scene.cycles.device = 'CPU'
    return 'CPU'


def setup_world(scene, sun_elev=45.0, sun_az=135.0, sun_strength=3.5, sky_strength=0.7):
    world = bpy.data.worlds.new('sky')
    scene.world = world
    nt = world.node_tree
    N, L = nt.nodes, nt.links
    bg = N['Background']
    d = Vector((math.cos(math.radians(sun_elev)) * math.sin(math.radians(sun_az)),
                math.cos(math.radians(sun_elev)) * math.cos(math.radians(sun_az)), math.sin(math.radians(sun_elev))))
    sky = N.new('ShaderNodeTexSky')
    try:
        sky.sky_type = 'HOSEK_WILKIE'
        sky.turbidity = 2.5
        sky.sun_direction = d
    except Exception:
        pass
    flat = N.new('ShaderNodeRGB')
    flat.outputs[0].default_value = (*eg.hex_rgb('#9FB4D0'), 1)
    lp = N.new('ShaderNodeLightPath')
    mix = N.new('ShaderNodeMix')
    mix.data_type = 'RGBA'
    L.new(lp.outputs['Is Camera Ray'], mix.inputs['Factor'])
    flat_s = N.new('ShaderNodeVectorMath')
    flat_s.operation = 'SCALE'
    flat_s.inputs['Scale'].default_value = sky_strength
    L.new(flat.outputs[0], flat_s.inputs[0])
    sky_s = N.new('ShaderNodeVectorMath')
    sky_s.operation = 'SCALE'
    sky_s.inputs['Scale'].default_value = 0.8
    L.new(sky.outputs['Color'], sky_s.inputs[0])
    L.new(flat_s.outputs[0], mix.inputs['A'])
    L.new(sky_s.outputs[0], mix.inputs['B'])
    L.new(mix.outputs['Result'], bg.inputs['Color'])
    bg.inputs['Strength'].default_value = 1.0
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = sun_strength
    sun.angle = math.radians(0.53)
    sun.color = (1.0, 0.97, 0.92)
    so = bpy.data.objects.new('sun', sun)
    eg.link(so)
    so.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
    return d


def ground(scene, size=600, c0='#4f5a2f', c1='#66693d', scale=0.08):
    m = bpy.data.materials.new('ground')
    nt = m.node_tree
    bsdf = nt.nodes['Principled BSDF']
    tex = nt.nodes.new('ShaderNodeTexNoise')
    tex.inputs['Scale'].default_value = scale
    tex.inputs['Detail'].default_value = 6
    ramp = nt.nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].color = (*eg.hex_rgb(c0), 1)
    ramp.color_ramp.elements[1].color = (*eg.hex_rgb(c1), 1)
    nt.links.new(tex.outputs['Fac'], ramp.inputs['Fac'])
    nt.links.new(ramp.outputs['Color'], bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value = 0.95
    bpy.ops.mesh.primitive_plane_add(size=size)
    g = bpy.context.active_object
    g.data.materials.append(m)
    return g


def _img(path, non_color=False):
    im = bpy.data.images.load(path, check_existing=True)
    if non_color:
        im.colorspace_settings.name = 'Non-Color'
    return im


def card_material(name, albedo, normal=None, mask=None, cutoff=0.5, smooth_max=0.8, translucent=0.15, cull_back=False,
                  normal_scale=1.0, specular=0.3):
    """Карточки (листва, «подкроны», крест): альфа-отсечение, карта нормалей, гладкость из маски, просвет листа."""
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    N.clear()
    out = N.new('ShaderNodeOutputMaterial')
    uv = N.new('ShaderNodeUVMap')
    uv.uv_map = 'UVMap'
    ta = N.new('ShaderNodeTexImage')
    ta.image = _img(albedo)
    ta.image.alpha_mode = 'STRAIGHT'
    L.new(uv.outputs[0], ta.inputs[0])
    bsdf = N.new('ShaderNodeBsdfPrincipled')
    L.new(ta.outputs['Color'], bsdf.inputs['Base Color'])
    bsdf.inputs['Specular IOR Level'].default_value = specular
    if normal:
        tn = N.new('ShaderNodeTexImage')
        tn.image = _img(normal, True)
        L.new(uv.outputs[0], tn.inputs[0])
        nm = N.new('ShaderNodeNormalMap')
        nm.uv_map = 'UVMap'
        nm.inputs['Strength'].default_value = normal_scale
        L.new(tn.outputs['Color'], nm.inputs['Color'])
        L.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    if mask:
        tm = N.new('ShaderNodeTexImage')
        tm.image = _img(mask, True)
        tm.image.alpha_mode = 'CHANNEL_PACKED'
        L.new(uv.outputs[0], tm.inputs[0])
        smx = N.new('ShaderNodeMath')
        smx.operation = 'MULTIPLY'
        smx.inputs[1].default_value = smooth_max
        L.new(tm.outputs['Alpha'], smx.inputs[0])
        inv = N.new('ShaderNodeMath')
        inv.operation = 'SUBTRACT'
        inv.inputs[0].default_value = 1.0
        L.new(smx.outputs[0], inv.inputs[1])
        L.new(inv.outputs[0], bsdf.inputs['Roughness'])
    else:
        bsdf.inputs['Roughness'].default_value = 1.0 - smooth_max
    surf = bsdf.outputs[0]
    if translucent > 0:
        trl = N.new('ShaderNodeBsdfTranslucent')
        L.new(ta.outputs['Color'], trl.inputs['Color'])
        mx = N.new('ShaderNodeMixShader')
        mx.inputs['Fac'].default_value = translucent
        L.new(surf, mx.inputs[1])
        L.new(trl.outputs[0], mx.inputs[2])
        surf = mx.outputs[0]
    clip = N.new('ShaderNodeMath')
    clip.operation = 'GREATER_THAN'
    clip.inputs[1].default_value = cutoff
    L.new(ta.outputs['Alpha'], clip.inputs[0])
    trn = N.new('ShaderNodeBsdfTransparent')
    ma = N.new('ShaderNodeMixShader')
    L.new(clip.outputs[0], ma.inputs['Fac'])
    L.new(trn.outputs[0], ma.inputs[1])
    L.new(surf, ma.inputs[2])
    surf = ma.outputs[0]
    if cull_back:
        geo = N.new('ShaderNodeNewGeometry')
        trb = N.new('ShaderNodeBsdfTransparent')
        mb = N.new('ShaderNodeMixShader')
        L.new(geo.outputs['Backfacing'], mb.inputs['Fac'])
        L.new(surf, mb.inputs[1])
        L.new(trb.outputs[0], mb.inputs[2])
        surf = mb.outputs[0]
    L.new(surf, out.inputs['Surface'])
    return m


def bark_material(name, albedo, normal, mask, normal_scale=1.0):
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    bsdf = N['Principled BSDF']
    uv = N.new('ShaderNodeUVMap')
    uv.uv_map = 'UVMap'
    ta = N.new('ShaderNodeTexImage')
    ta.image = _img(albedo)
    L.new(uv.outputs[0], ta.inputs[0])
    L.new(ta.outputs['Color'], bsdf.inputs['Base Color'])
    tn = N.new('ShaderNodeTexImage')
    tn.image = _img(normal, True)
    L.new(uv.outputs[0], tn.inputs[0])
    nm = N.new('ShaderNodeNormalMap')
    nm.uv_map = 'UVMap'
    nm.inputs['Strength'].default_value = normal_scale
    L.new(tn.outputs['Color'], nm.inputs['Color'])
    L.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    tm = N.new('ShaderNodeTexImage')
    tm.image = _img(mask, True)
    tm.image.alpha_mode = 'CHANNEL_PACKED'
    L.new(uv.outputs[0], tm.inputs[0])
    inv = N.new('ShaderNodeMath')
    inv.operation = 'SUBTRACT'
    inv.inputs[0].default_value = 1.0
    L.new(tm.outputs['Alpha'], inv.inputs[1])
    L.new(inv.outputs[0], bsdf.inputs['Roughness'])
    bsdf.inputs['Specular IOR Level'].default_value = 0.3
    return m


def materials_for(folder, man):
    mats = {}
    T = lambda n: os.path.join(folder, n)  # noqa: E731
    for e in man['materials']:
        if e['role'] == 'bark':
            mats[e['id']] = bark_material(e['id'], T(e['albedo']), T(e['normal']), T(e['mask']), e.get('normalScale', 1.0))
        elif e['role'] == 'cross':
            mats[e['id']] = card_material(e['id'], T(e['albedo']), T(e['normal']), None, e.get('alphaCutoff', 0.5),
                                          smooth_max=e.get('smoothnessMax', 0.15), translucent=0.08, cull_back=True)
        else:
            mats[e['id']] = card_material(e['id'], T(e['albedo']), T(e['normal']), T(e['mask']), e.get('alphaCutoff', 0.5),
                                          smooth_max=e.get('smoothnessMax', 0.8), translucent=0.15,
                                          normal_scale=e.get('normalScale', 1.0))
    return mats


def load_asset(root, name):
    folder = os.path.join(root, name)
    with open(os.path.join(folder, 'manifest.json'), encoding='utf-8') as f:
        man = json.load(f)
    mats = materials_for(folder, man)
    lods = []
    for lod in man['lods']:
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=os.path.join(folder, lod['fbx']), axis_forward='-Z', axis_up='Y')
        objs = [o for o in bpy.data.objects if o not in before and o.type == 'MESH']
        o = objs[0]
        for i, mid in enumerate(lod['submeshes']):
            if i < len(o.data.materials):
                o.data.materials[i] = mats[mid]
            else:
                o.data.materials.append(mats[mid])
        o.data.polygons.foreach_set('use_smooth', [True] * len(o.data.polygons))
        o.data.transform(o.matrix_world)
        o.matrix_world = Matrix.Identity(4)
        o.location = (0, 0, -1000)
        lods.append(o)
    bb = [Vector(c) for c in lods[0].bound_box]
    size = max(max(v.x for v in bb) - min(v.x for v in bb), max(v.y for v in bb) - min(v.y for v in bb),
               max(v.z for v in bb) - min(v.z for v in bb))
    W = max(max(v.x for v in bb) - min(v.x for v in bb), max(v.y for v in bb) - min(v.y for v in bb))
    return dict(name=name, lods=lods, H=man['height'], W=W, size=size, man=man,
                centre_z=0.5 * (max(v.z for v in bb) + min(v.z for v in bb)),
                screen=[l['screen'] for l in man['lods']])


def camera(scene, loc, target, fov=FOV_LOD, res=(1280, 720), clip=5000):
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    eg.link(cam)
    cam.data.sensor_fit = 'VERTICAL'
    cam.data.angle_y = math.radians(fov)
    cam.data.clip_start = 0.02
    cam.data.clip_end = clip
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    scene.camera = cam
    scene.render.resolution_x, scene.render.resolution_y = res
    scene.render.resolution_percentage = 100
    return cam


def place(asset, lod, loc, yaw=0.0, scale=1.0):
    src = asset['lods'][lod]
    o = bpy.data.objects.new(f"{asset['name']}_{lod}", src.data)
    eg.link(o)
    o.matrix_world = Matrix.Translation(loc) @ Matrix.Rotation(yaw, 4, 'Z') @ Matrix.Scale(scale, 4)
    return o


def lod_for(asset, loc, scale, cam_loc, fov=FOV_LOD):
    """Как Unity LODGroup: доля высоты экрана = размер / (2 d tan(fov/2)), d — до центра рамки."""
    size = asset['size'] * scale
    centre = Vector(loc) + Vector((0, 0, asset['centre_z'] * scale))
    d = (centre - Vector(cam_loc)).length
    s = size / (2 * d * math.tan(math.radians(fov) / 2))
    for i, th in enumerate(asset['screen']):
        if s >= th:
            return i
    return len(asset['screen']) - 1


def render(scene, path, samples=96, transparent=False):
    scene.render.engine = 'CYCLES'
    use_gpu(scene)
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.cycles.transparent_max_bounces = 64
    scene.cycles.max_bounces = 6
    scene.render.film_transparent = transparent
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0.0
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA' if transparent else 'RGB'
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print('PREVIEW', path)


def stand_layout(rng, n, spacing, jitter):
    side = int(math.ceil(math.sqrt(n)))
    pts = []
    for i in range(side):
        for j in range(side):
            if len(pts) < n:
                pts.append(Vector(((i - side / 2 + 0.5) * spacing + rng.uniform(-jitter, jitter),
                                   (j - side / 2 + 0.5) * spacing + rng.uniform(-jitter, jitter), 0)))
    return pts


def lod_numbers(scene, asset, path):
    """Покрытие (доля непрозрачного) и средняя яркость кроны каждого LOD: вид сбоку и сверху под 40°, орто."""
    import numpy as np
    H, W = asset['H'], asset['W']
    res = {}
    for o in bpy.data.objects:
        if o.name.startswith('Plane'):
            o.hide_render = True
    for view, loc, rot in (('side', (0, -300, H * 0.5), (math.pi / 2, 0, 0)),
                           ('air40', (0, -300 * math.cos(math.radians(40)), 300 * math.sin(math.radians(40)) + H * 0.5),
                            (math.radians(50), 0, 0))):
        cam = bpy.data.objects.new('ocam', bpy.data.cameras.new('ocam'))
        eg.link(cam)
        cam.data.type = 'ORTHO'
        cam.data.ortho_scale = max(H, W) * 1.15
        cam.data.clip_end = 2000
        cam.location = loc
        cam.rotation_euler = rot
        scene.camera = cam
        scene.render.resolution_x = scene.render.resolution_y = 400
        # i: LOD0..3 с тенями; 4, 5: LOD2 и LOD3 без теней — как в игре дальше 70 м (Nature Renderer не рисует теней)
        for i, ns in ((0, False), (1, False), (2, False), (3, False), (2, True), (3, True)):
            o = place(asset, i, (0, 0, 0))
            o.visible_shadow = not ns
            p = path + f'_{view}_{i}.png'
            render(scene, p, 32, transparent=True)
            bpy.data.objects.remove(o)
            img = bpy.data.images.load(p)
            px = np.array(img.pixels[:], np.float32).reshape(400, 400, 4)
            bpy.data.images.remove(img)
            a = px[..., 3] > 0.5
            rgb = px[..., :3]
            lin = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
            lum = (lin * np.array([0.2126, 0.7152, 0.0722])).sum(-1)
            ys, xs = np.nonzero(a)
            res.setdefault(view, []).append(dict(lod=i, noshadow=ns, coverage=round(float(a.mean()), 4),
                                                 lum=round(float(lum[a].mean()) if a.any() else 0, 4),
                                                 top=int(ys.max()) if len(ys) else 0, width=int(xs.max() - xs.min()) if len(xs) else 0))
            os.remove(p)
        bpy.data.objects.remove(cam)
    with open(path + '.json', 'w', encoding='utf-8') as f:
        json.dump(res, f, indent=1)
    print('LODNUM', json.dumps(res))
    return res


def main():
    args = eg.script_args()
    root, mode, out = args[0], args[1], args[2]
    names = [a for a in args[3:] if not a.startswith('--')] or ['oak_forest_01']
    scene = eg.reset_scene()
    assets = [load_asset(root, n) for n in names]
    setup_world(scene)
    a0 = assets[0]
    if mode == 'close':
        ground(scene)
        place(a0, 0, (0, 0, 0))
        H = a0['H']
        camera(scene, (2.1, -2.1, 1.5), (0.0, 0.0, min(H * 0.55, 10.0)), fov=80)
        render(scene, out, 128)
    elif mode == 'lens':
        ground(scene)
        place(a0, 0, (0, 0, 0))
        tgt = Vector((0.0, 0.0, 0.10)) + Vector((0.0, 1.0, math.tan(math.radians(25)))) * 1.0
        camera(scene, (0.0, -1.0, 0.10), tgt + Vector((0, -1.0, 0)) + Vector((0, 1.0, 0)), fov=80)
        render(scene, out, 128)
    elif mode == 'medium':
        ground(scene)
        cam_loc = Vector((0, -25, 2.0))
        lod = lod_for(a0, (0, 0, 0), 1.0, cam_loc)
        place(a0, lod, (0, 0, 0))
        print('LOD', lod)
        camera(scene, cam_loc, (0, 0, a0['H'] * 0.47), fov=50)
        render(scene, out, 128)
    elif mode == 'lineup':
        ground(scene)
        x = 0.0
        xs = []
        for a in assets:
            x += a['W'] * 0.5
            xs.append(x)
            x += a['W'] * 0.5 + 2.0
        for a, xx in zip(assets, xs):
            place(a, 0, (xx - x * 0.5, 0, 0))
        Hm = max(a['H'] for a in assets)
        dist = x * 0.95
        camera(scene, (0, -dist, 2.0), (0, 0, Hm * 0.42), fov=40, res=(1800, 700))
        render(scene, out, 128)
    elif mode == 'lods':
        ground(scene)
        H, W = a0['H'], a0['W']
        gap = W * 1.08
        for i in range(4):
            place(a0, i, ((i - 1.5) * gap, 0, 0))
        dist = max(3.2 * gap, 2.05 * H)
        camera(scene, (0, -dist, H * 0.5), (0, 0, H * 0.5), fov=30, res=(1800, 700))
        render(scene, out, 96)
        for o in list(bpy.data.objects):
            if o.name.startswith(a0['name'] + '_') and o.data in [l.data for l in a0['lods']] and o not in a0['lods']:
                bpy.data.objects.remove(o)
        lod_numbers(scene, a0, os.path.splitext(out)[0])
    elif mode == 'lodpair':
        # LOD n (слева) и n+1 (справа) на расстоянии смены n -> n+1 (FOV 80°), кадр приближен к паре
        ground(scene, 3000)
        size = a0['size']
        base = os.path.splitext(out)[0]
        for k in range(3):
            d = size / (2 * math.tan(math.radians(FOV_LOD) / 2) * a0['screen'][k])
            gap = a0['W'] * 0.6 + 1.0
            objs = [place(a0, k, (-gap, d, 0)), place(a0, k + 1, (gap, d, 0))]
            cam_loc = Vector((0, 0, 2.0))
            fov = math.degrees(2 * math.atan(1.25 * max(size, 2 * gap + a0['W']) / (2 * d)))
            camera(scene, cam_loc, (0, d, a0['H'] * 0.48), fov=fov, res=(1280, 720))
            render(scene, f'{base}_{k}{k + 1}.png', 96)
            for o in objs:
                bpy.data.objects.remove(o)
    elif mode[:5] in ('stand', 'field', 'cross'):
        alt = float(mode[5:])
        ground(scene, 3000, '#4a4a2e' if mode.startswith('stand') else '#56603a', '#5f5f38' if mode.startswith('stand') else '#6b6d42')
        rng = random.Random(7)
        forest = [a for a in assets if 'forest' in a['name']] or assets
        opens = [a for a in assets if 'open' in a['name']]
        placed = []
        look = 40.0
        cam_loc = Vector((0, -alt / math.tan(math.radians(look)), alt))
        if mode.startswith('stand') or mode.startswith('cross'):
            for p in stand_layout(rng, 16, 9.0, 2.0):
                placed.append((rng.choice(forest), p))
        else:
            for p in stand_layout(rng, 16, 9.0, 2.0):
                placed.append((rng.choice(forest), p + Vector((0, 34, 0))))
            for k in range(4):
                a = opens[k % len(opens)] if opens else forest[0]
                placed.append((a, Vector((-42 + 28 * k + rng.uniform(-4, 4), -16 + rng.uniform(-8, 8), 0))))
        lod_count = [0, 0, 0, 0]
        for a, p in placed:
            s = rng.uniform(0.85, 1.15)
            lod = 3 if mode.startswith('cross') else lod_for(a, p, s, cam_loc)
            lod_count[lod] += 1
            place(a, lod, p, rng.uniform(0, math.tau), s)
        print('LODS', lod_count)
        target = Vector((0, 8 if mode.startswith('field') else 0, 4))
        camera(scene, cam_loc, target, fov=24 if alt < 100 else 14, res=(1600, 900))
        render(scene, out, 128)
        if '--wide' in args:
            camera(scene, cam_loc, target, fov=FOV_LOD, res=(1600, 900))
            render(scene, os.path.splitext(out)[0] + '_wide.png', 96)
    print('DONE')


if __name__ == '__main__':
    main()
