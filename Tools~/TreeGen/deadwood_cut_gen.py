# Спиленная древесина для леса (Tools/TreeGen; дизайн леса, этап 3): пни, распиленные брёвна и штабель — то, по чему
# в наших лесах видна рубка (санитарные и сплошные рубки, заготовка у лесных дорог). Сканы Poly Haven (deadwood_gen.py)
# дают только старые гнилые пни, ушедшие в бугор земли; спиленный пень со светлым срезом с дрона виден светлой точкой.
# Геометрия — своя, процедурная (MeshBuilder): пень — ствол с корневыми наплывами у земли (3–5 лопастей, наплыв гаснет за
# ~10 см) и наклонным спилом (3–8°); бревно — чуть сбежистое, изогнутое, неровное по кругу; штабель — 12 брёвен рядами
# 5–4–3 с разбросом торцов. Материал — один на породу (атлас 2048 × 1024, роль bark, NM_Bark NR, ветер выключен):
# слева (U 0–0.5) кора Poly Haven (CC0: bark_willow_02 — бурая бороздчатая, как у дуба и тополя; pine_bark — рыжие
# пластины сосны), 2 м на текстуру, по кругу ствола — ровно вся ширина (кора тайлится, шва нет), вдоль — с повтором по V;
# справа — четыре торца 512² (свой, процедурный рисунок): свежий спил, второй свежий, выветренный (серый, трещины
# усушки), гнилой (тёмный, мягкий, мох по краю). Торец: годичные кольца вокруг смещённой сердцевины (ранняя/поздняя
# древесина, кольца по кругу волнистые), ядро темнее заболони, кольцо коры по краю, следы пилы; рельеф — по высоте
# (поздняя древесина выше, у выветренного сильнее, трещины глубоко) в нормали OpenGL. Цвет — в палитру сцены
# (envgen_common.grade_albedo): кора — к средней мёртвой древесины (чуть темнее и серее коры бука, тополя, дуба сцены,
# яркость ~0.08), торцы — приглушённые (свежий ~0.13, выветренный ~0.1, гнилой ~0.05): с цветом фото и ярким спилом пень
# и бревно выбивались из леса, как чужие. Маска NatureManufacture: R 0, G AO, B высота, A гладкость.
# Пивот — земля: пень уходит на 12 см в неё, бревно и штабель — на 2 см. Пням — ещё и объект с коллайдером (<name>_prop)
# для точки взлёта.
#  - deadwood_cut_stump_01..04 — пни: дуб свежий (Ø 0.44 м, 0.35 м), дуб выветренный (Ø 0.56, 0.5), сосна свежая (Ø 0.36,
#    0.3), сосна гнилая (Ø 0.48, 0.45);
#  - deadwood_sawn_log_01, _02 — брёвна: дуб 3.5 м Ø 0.36, сосна 4 м Ø 0.3 (свежие торцы);
#  - deadwood_log_stack_01 — штабель сосны: 12 брёвен ~3.8 м Ø 0.2–0.3.
# Запуск (Blender 5.2, фоном):
#   blender.exe -b --factory-startup --python deadwood_cut_gen.py -- --bark <папка с bark_willow_02/ и pine_bark/> --out <папка>
import argparse
import json
import math
import os
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

GROUP = 'Deadwood'
W, H = 2048, 1024
Q = 512
BARK_METRES = 2.0
# торцы в атласе: левый нижний угол квадранта (U, V)
QUADS = {'fresh': (0.5, 0.5), 'weathered': (0.75, 0.5), 'fresh2': (0.5, 0.0), 'rotten': (0.75, 0.0)}
SPECIES = {
    'oak': dict(bark='bark_willow_02', sap=(0.55, 0.42, 0.27), heart=(0.40, 0.26, 0.15), rings=(34, 48),
                bark_tone=(0.085, 0.077, 0.066), fresh_tone=(0.16, 0.125, 0.085)),
    'pine': dict(bark='pine_bark', sap=(0.62, 0.50, 0.32), heart=(0.52, 0.34, 0.18), rings=(26, 40),
                 bark_tone=(0.088, 0.068, 0.052), fresh_tone=(0.17, 0.13, 0.085)),
}
# торцы в палитре сцены (линейные средние диска); насыщенность коры и торцов
END_TONES = {'weathered': (0.105, 0.1, 0.09), 'rotten': (0.06, 0.047, 0.034)}
GRADE_SATURATION = 0.55


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def load_pixels(path, size):
    image = bpy.data.images.load(path, check_existing=False)
    image.colorspace_settings.name = 'Non-Color'
    image.scale(size, size)
    px = np.array(image.pixels[:], np.float32).reshape(size, size, 4)[::-1].copy()
    bpy.data.images.remove(image)
    return px


def end_grain(kind, species, seed):
    """Торец 512²: (альбедо линейно, нормаль 0..1, маска). Диск — до радиуса 0.96 квадранта."""
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:Q, 0:Q].astype(np.float32)
    u = (xx + 0.5) / Q * 2 - 1
    v = 1 - (yy + 0.5) / Q * 2
    r = np.hypot(u, v) / 0.96
    cx, cy = rng.uniform(-0.14, 0.14, 2)
    dx, dy = u / 0.96 - cx, v / 0.96 - cy
    rp = np.hypot(dx, dy)
    ang = np.arctan2(dy, dx)
    warp = np.zeros_like(rp)
    for k in (2, 3, 5, 7):
        warp += rng.uniform(0.008, 0.025) * np.sin(k * ang + rng.uniform(0, 6.28))
    rings = rng.uniform(*species['rings'])
    t = rp * (1 + warp) * rings
    frac = t - np.floor(t)
    late = smoothstep(0.62, 0.9, frac) * (1 - smoothstep(0.93, 1.0, frac))
    sap = np.array(species['sap'], np.float32)
    heart = np.array(species['heart'], np.float32)
    heartwood = 1 - smoothstep(0.5, 0.62, rp)
    base = heart * heartwood[..., None] + sap * (1 - heartwood[..., None])
    colour = base * (1 - 0.22 * late[..., None])
    height = 0.5 + 0.04 * late
    cracks = np.zeros_like(rp)
    smooth = np.full_like(rp, 0.25)
    if kind in ('weathered', 'rotten'):
        for _ in range(rng.integers(5, 10)):
            phi = rng.uniform(-math.pi, math.pi)
            length = rng.uniform(0.35, 0.95)
            d = np.abs(np.angle(np.exp(1j * (ang - phi)))) * rp
            width = 0.004 + 0.018 * rp
            cracks = np.maximum(cracks, smoothstep(width, 0.0, d) * (rp > 0.06) * (1 - smoothstep(length - 0.1, length, rp)))
    if kind == 'weathered':
        grey = np.array([0.30, 0.28, 0.25], np.float32)
        colour = grey * (0.85 + 0.15 * (1 - late[..., None])) * (1 - 0.15 * heartwood[..., None])
        height = 0.5 + 0.12 * late
        smooth[:] = 0.18
    elif kind == 'rotten':
        rot = np.array([0.16, 0.11, 0.07], np.float32)
        colour = rot * (0.8 + 0.4 * (1 - late[..., None])) * (1 + 0.25 * heartwood[..., None])
        moss = smoothstep(0.7, 0.95, r) * smoothstep(0.35, 0.65, 0.5 + 0.5 * np.sin(ang * 5 + rp * 9))
        colour = colour * (1 - moss[..., None]) + np.array([0.09, 0.11, 0.045], np.float32) * moss[..., None]
        height = 0.45 + 0.08 * late - 0.1 * heartwood
        smooth[:] = 0.12
    else:
        # следы пилы: параллельные дуги поперёк спила
        a = rng.uniform(0, math.pi)
        saw = np.sin((u * math.cos(a) + v * math.sin(a)) * rng.uniform(45, 70) + 3 * np.sin(u * 3 + v * 2))
        colour = colour * (1 + 0.03 * saw[..., None])
        height = height + 0.015 * saw
        colour = colour * 0.75
    colour = colour * (1 - 0.85 * cracks[..., None])
    height = height - 0.4 * cracks
    # кора по краю диска и за ним
    bark_ring = smoothstep(0.9, 0.97, r)
    bark_colour = np.array([0.09, 0.07, 0.05], np.float32)
    colour = colour * (1 - bark_ring[..., None]) + bark_colour * bark_ring[..., None]
    height = height * (1 - bark_ring) + 0.55 * bark_ring
    gy, gx = np.gradient(height)
    strength = 6.0
    n = np.stack([-gx * strength, gy * strength, np.ones_like(gx)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    normal = n * 0.5 + 0.5
    ao = 1 - 0.6 * cracks
    mask = np.stack([np.zeros_like(ao), ao, np.clip(height, 0, 1), smooth * (1 - bark_ring) + 0.15 * bark_ring], -1)
    return colour, normal, mask, (r < 0.9).astype(np.float32)


def build_atlas(species_name, bark_root, out):
    species = SPECIES[species_name]
    bark = os.path.join(bark_root, species['bark'], species['bark'])
    diff = load_pixels(bark + '_diff.jpg', H)
    nor = load_pixels(bark + '_nor_gl.jpg', H)
    arm = load_pixels(bark + '_arm.jpg', H)
    albedo = np.zeros((H, W, 3), np.float32)
    normal = np.zeros((H, W, 3), np.float32)
    mask = np.zeros((H, W, 4), np.float32)
    albedo[:, :H] = eg.grade_albedo(srgb_to_linear(diff[..., :3]), species['bark_tone'], GRADE_SATURATION)
    normal[:, :H] = nor[..., :3]
    mask[:, :H, 1] = arm[..., 0]
    mask[:, :H, 2] = 0.5
    mask[:, :H, 3] = 1 - arm[..., 1]
    seed = 11 if species_name == 'oak' else 23
    for i, (kind, (qu, qv)) in enumerate(QUADS.items()):
        grain = 'fresh' if kind == 'fresh2' else kind
        colour, nrm, msk, disc = end_grain(grain, species, seed + i)
        colour = eg.grade_albedo(colour, species['fresh_tone'] if grain == 'fresh' else END_TONES[grain], GRADE_SATURATION, disc)
        col0 = int(qu * W)
        row0 = int((1 - qv) * H) - Q
        albedo[row0:row0 + Q, col0:col0 + Q] = colour
        normal[row0:row0 + Q, col0:col0 + Q] = nrm
        mask[row0:row0 + Q, col0:col0 + Q] = msk
    name = 'deadwood_cut_' + species_name
    eg.save_png(albedo, os.path.join(out, name + '_albedo.png'))
    eg.save_png(normal, os.path.join(out, name + '_normal.png'), linear=True)
    eg.save_png(mask, os.path.join(out, name + '_mask.png'), linear=True)
    return name


NO_WIND = (1.0, 1.0, 1.0, 0.0)


def disc_uv(kind, rho, theta):
    qu, qv = QUADS[kind]
    return (qu + 0.125 + rho * math.cos(theta) * 0.12, qv + 0.25 + rho * math.sin(theta) * 0.24)


def add_cap(m, centre, rim, kind, flip, pith=(0.0, 0.0)):
    """Торец: веер от центра к кольцу rim (список вершин по кругу)."""
    n = len(rim)
    c = m.vert(centre)
    ring = [m.vert(p) for p in rim]
    for j in range(n):
        a, b = ring[j], ring[(j + 1) % n]
        ta, tb = 2 * math.pi * j / n, 2 * math.pi * ((j + 1) % n) / n
        uvs = [disc_uv(kind, 0.0, 0.0), disc_uv(kind, 1.0, ta), disc_uv(kind, 1.0, tb)]
        if flip:
            m.face([c, b, a], 0, [uvs[0], uvs[2], uvs[1]], colors=[NO_WIND] * 3)
        else:
            m.face([c, a, b], 0, uvs, colors=[NO_WIND] * 3)


def stump_mesh(rng, r0, h, tilt_deg, lobes, cap, segs=24):
    m = eg.MeshBuilder()
    lobe_angles = rng.uniform(0, 2 * math.pi, lobes)
    tilt_dir = rng.uniform(0, 2 * math.pi)
    tilt = math.tan(math.radians(tilt_deg))
    phases = rng.uniform(0, 2 * math.pi, 3)
    levels = [-0.12, 0.0, 0.05, 0.12, 0.25, 0.45, 0.7, 1.0]
    v_offset = rng.uniform(0, 1)

    def top(theta):
        return h + r0 * tilt * math.cos(theta - tilt_dir)

    def radius(z, theta):
        lobe = 0.2 + 0.8 * max(max(0.0, math.cos(theta - a)) ** 5 for a in lobe_angles)
        flare = 0.8 * math.exp(-max(z, 0.0) / 0.09) if z < 0.3 else 0.0
        bumps = 1 + 0.035 * (math.sin(3 * theta + phases[0]) + 0.6 * math.sin(5 * theta + phases[1] + z * 7))
        return r0 * bumps * (1 + flare * lobe) * (1 + 0.15 * max(-z, 0) / 0.12)

    grid = []
    for k, s in enumerate(levels):
        row = []
        for j in range(segs + 1):
            theta = 2 * math.pi * j / segs
            z = -0.12 if k == 0 else top(theta) * s if s > 0 else 0.0
            r = radius(z, theta)
            row.append((m.vert((r * math.cos(theta), r * math.sin(theta), z)), theta, z))
        grid.append(row)
    for k in range(len(levels) - 1):
        for j in range(segs):
            a, b = grid[k][j], grid[k][j + 1]
            c, d = grid[k + 1][j + 1], grid[k + 1][j]
            uv = [(p[1] / (2 * math.pi) * 0.5, p[2] / BARK_METRES + v_offset) for p in (a, b, c, d)]
            m.face([a[0], b[0], c[0], d[0]], 0, uv, colors=[NO_WIND] * 4)
    rim = []
    for j in range(segs):
        theta = 2 * math.pi * j / segs
        z = top(theta)
        r = radius(z, theta)
        rim.append((r * math.cos(theta), r * math.sin(theta), z))
    add_cap(m, (0.0, 0.0, h), rim, cap, flip=False)
    return m


def log_mesh(rng, length, r, taper, caps, segs=14, offset=(0.0, 0.0, 0.0), yaw=0.0, bow=0.04):
    m = eg.MeshBuilder()
    n_len = max(4, int(length / 0.35))
    phases = rng.uniform(0, 2 * math.pi, 3)
    v_offset = rng.uniform(0, 1)
    cy, sy = math.cos(yaw), math.sin(yaw)

    def place(x, y, z):
        return (offset[0] + x * cy - y * sy, offset[1] + x * sy + y * cy, offset[2] + z)

    def rad(t, theta):
        return r * (1 + (taper - 1) * t) * (1 + 0.03 * math.sin(2 * theta + phases[0]) + 0.02 * math.sin(3 * theta + phases[1] + t * 9))

    grid = []
    for i in range(n_len + 1):
        t = i / n_len
        x = -length / 2 + length * t
        bend = bow * math.sin(math.pi * t)
        row = []
        for j in range(segs + 1):
            theta = 2 * math.pi * j / segs
            rr = rad(t, theta)
            row.append((m.vert(place(x, rr * math.cos(theta) + bend, rr * math.sin(theta))), theta, x))
        grid.append(row)
    for i in range(n_len):
        for j in range(segs):
            a, b = grid[i][j], grid[i][j + 1]
            c, d = grid[i + 1][j + 1], grid[i + 1][j]
            uv = [(p[1] / (2 * math.pi) * 0.5, (p[2] + length / 2) / BARK_METRES + v_offset) for p in (a, b, c, d)]
            m.face([a[0], b[0], c[0], d[0]], 0, uv, colors=[NO_WIND] * 4)
    for end, t, flip in ((0, 0.0, True), (1, 1.0, False)):
        x = -length / 2 + length * t
        bend = bow * math.sin(math.pi * t)
        rim = []
        for j in range(segs):
            theta = 2 * math.pi * j / segs
            rr = rad(t, theta)
            rim.append(place(x, rr * math.cos(theta) + bend, rr * math.sin(theta)))
        add_cap(m, place(x, bend, 0.0), rim, caps[end], flip=flip)
    return m


def stack_mesh(rng, length=3.8):
    m = eg.MeshBuilder()
    rows = [5, 4, 3]
    radius = 0.125
    z = radius
    for row, count in enumerate(rows):
        width = (count - 1) * 2 * radius
        for i in range(count):
            r = radius * rng.uniform(0.82, 1.12)
            y = -width / 2 + i * 2 * radius + rng.uniform(-0.015, 0.015)
            x = rng.uniform(-0.18, 0.18)
            log = log_mesh(rng, length * rng.uniform(0.96, 1.04), r, rng.uniform(0.86, 0.95),
                           caps=('fresh' if rng.random() < 0.6 else 'fresh2', 'fresh2' if rng.random() < 0.5 else 'fresh'),
                           segs=12, offset=(x, y, z - (radius - r)), yaw=rng.uniform(-0.03, 0.03), bow=0.015)
            m.append(log)
        z += radius * math.sqrt(3)
    return m


ASSETS = [
    dict(name='deadwood_cut_stump_01', prop=True, species='oak', kind='stump', r=0.22, h=0.35, tilt=4, lobes=4, cap='fresh', seed=101),
    dict(name='deadwood_cut_stump_02', prop=True, species='oak', kind='stump', r=0.28, h=0.5, tilt=7, lobes=5, cap='weathered', seed=102),
    dict(name='deadwood_cut_stump_03', prop=True, species='pine', kind='stump', r=0.18, h=0.3, tilt=3, lobes=3, cap='fresh', seed=103),
    dict(name='deadwood_cut_stump_04', prop=True, species='pine', kind='stump', r=0.24, h=0.45, tilt=6, lobes=4, cap='rotten', seed=104),
    dict(name='deadwood_sawn_log_01', species='oak', kind='log', length=3.5, r=0.18, taper=0.85, caps=('fresh', 'fresh2'), seed=201),
    dict(name='deadwood_sawn_log_02', species='pine', kind='log', length=4.0, r=0.15, taper=0.82, caps=('fresh2', 'fresh'), seed=202),
    dict(name='deadwood_log_stack_01', species='pine', kind='stack', seed=301),
]


def material_entry(material):
    return dict(id=material, role='bark', albedo=material + '_albedo.png', normal=material + '_normal.png',
                mask=material + '_mask.png', tiling=[1, 1], normalScale=1.0, smoothnessMax=1.0, aoMax=1.0, stiffness=1.0,
                drag=0.0001, initialBend=0.0, heightDrag=5.0)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--bark', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--assets', nargs='*')
    args = parser.parse_args(eg.script_args())
    eg.reset_scene()
    atlas_dir = os.path.join(args.out, '_atlas')
    os.makedirs(atlas_dir, exist_ok=True)
    materials = {s: build_atlas(s, args.bark, atlas_dir) for s in SPECIES}
    for spec in ASSETS:
        if args.assets and spec['name'] not in args.assets:
            continue
        eg.reset_scene()
        rng = np.random.default_rng(spec['seed'])
        if spec['kind'] == 'stump':
            m = stump_mesh(rng, spec['r'], spec['h'], spec['tilt'], spec['lobes'], spec['cap'])
            distance = 160.0
        elif spec['kind'] == 'log':
            m = log_mesh(rng, spec['length'], spec['r'], spec['taper'], spec['caps'], offset=(0.0, 0.0, spec['r'] - 0.02))
            distance = 220.0
        else:
            m = stack_mesh(rng)
            for i, v in enumerate(m.verts):
                m.verts[i] = (v[0], v[1], v[2] - 0.02)
            distance = 300.0
        material = materials[spec['species']]
        obj = m.to_object(spec['name'], [bpy.data.materials.new(material)])
        eg.smooth_normals(obj, 50.0)
        out = os.path.join(args.out, spec['name'])
        os.makedirs(out, exist_ok=True)
        fbx = spec['name'] + '.fbx'
        eg.export_fbx([obj], os.path.join(out, fbx))
        for suffix in ('_albedo.png', '_normal.png', '_mask.png'):
            src = os.path.join(atlas_dir, material + suffix)
            with open(src, 'rb') as f_in, open(os.path.join(out, material + suffix), 'wb') as f_out:
                f_out.write(f_in.read())
        co = np.array(m.verts, np.float32)
        manifest = dict(name=spec['name'], type='detail', group=GROUP, height=float(co[:, 2].max()), trunkRadius=0.0,
                        colliderHeight=0.0, evergreen=False,
                        lods=[dict(fbx=fbx, screen=0.0, submeshes=[material], castShadows=True, triangles=m.triangle_count)],
                        materials=[material_entry(material)],
                        nr=dict(renderDistance=distance, shadows=True, densityInDistance=1.0),
                        size=[round(float(x), 3) for x in co.max(0) - co.min(0)], triangles=m.triangle_count,
                        source='procedural; bark Poly Haven ' + SPECIES[spec['species']]['bark'] + ' (CC0)')
        with open(os.path.join(out, 'manifest.json'), 'w', encoding='utf-8') as f:
            json.dump(manifest, f, indent=1)
        if spec.get('prop'):
            eg.write_prop_twin(out, manifest)
        print('DEADWOOD_CUT', spec['name'], json.dumps(dict(triangles=m.triangle_count, size=manifest['size'])))


main()
