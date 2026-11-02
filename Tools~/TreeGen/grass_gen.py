# Короткая густая трава (как MEKA Nanite Grass, но без Nanite): куртина из настоящих травинок — сужающиеся полоски
# с изгибом, без альфа-карточек. Цвет — из маленького атласа полос (вариант цвета на травинку, от комля к кончику), поэтому
# альфы нет. Вес ветра — альфа цвета вершин по высоте над своим корнем (корни прижаты, материал _AlphaDrag = 1).
# Нормали — в основном «вверх» с завёрнутым краем травинки: ковёр освещён мягко, без чёрных изнанок.
# Виды (дизайн, раздел 4; план размещения, разделы 3 N8 и 4):
#  - grass_short_sward — сочная низкая дернина (мятлик, райграс, овсяница луговая, полевица): основа некошеного луга;
#  - grass_short_sward_dry — сухая степная дернина (овсяница валисская, мятлик узколистный, тонконог): сухие склоны;
#  - grass_thatch — ветошь: лежащая прошлогодняя солома, сухие пеньки стеблей и редкие зелёные побеги;
#  - grass_tall_tuft — степная дерновина (овсяница, ковыль-тырса): узкие щетиновидные листья фонтаном от центра,
#    соломины со светлой узкой метёлкой из колосков;
#  - grass_mown_turf — скошенный газон (дворы, обочины, выбитый выгон): 3–8 см, листья 4–9 мм (пырей, райграс, мятлик),
#    половина со срезанным тупым концом (четырёхугольник), отросшие — острые (один треугольник); пятно ~1.8 м (акцент
#    поверх зелёной земли, не ковёр числом экземпляров).
# Пятно дернины, газона и ветоши — ковровое (ревью второго прохода): 1–2 лопасти с неровным краем (гармоники по углу),
# плотность ровная внутри и мягко спадает только у края, к краю травинки ниже, одиночные за краем. Лопастная плотность
# с пиком в центрах лопастей (раздел 4 плана) давала островки и проплешины; пятно больше (дернина и газон ~1.8 м,
# ветошь ~1.2 м) — при плотностях плана экземпляры сливаются, остальное даёт зелёная земля террейна. Дерновина
# (tall_tuft) — одно растение, у неё прежнее лопастное пятно с пиком в центре. Число травинок — по бюджету треугольников.
# Четверть корней даёт кущение (3–4 травинки рядом), без общего «зачёса». Дальность и тени Nature Renderer — в манифесте.
# Мёртвая ветошь по сезону не перекрашивается (seasonKeep 2), у ковров sizeVariation ≤ 0.12 (шейдер уменьшает весь
# экземпляр пятнами по 6 м — вместе с площадью).
# Запуск:  blender.exe -b --factory-startup --python grass_gen.py -- <папка вывода> [пресет ...]
import json
import math
import os
import random
import sys
import zlib

import numpy as np
from mathutils import Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

# Полосы атласа (16 штук по ширине): (основание, середина, кончик) sRGB. Летняя трава лесостепи — серовато-оливковая.
STRIPS = [
    ('#3a4826', '#56653a', '#7c8452'),  # 0 обычная
    ('#344223', '#4f5f34', '#71794a'),  # 1
    ('#404e2b', '#5f6e40', '#87905c'),  # 2 светлая
    ('#2f3c1f', '#46552d', '#646d40'),  # 3 тёмная
    ('#454b28', '#676b3f', '#918e5c'),  # 4 желтоватая (сухой склон)
    ('#384624', '#586a3c', '#7d8a58'),  # 5
    ('#4d4f2c', '#757044', '#9e9264'),  # 6 подсыхающая
    ('#3b4926', '#58673a', '#7e8654'),  # 7
    ('#4a4d2e', '#6e6c45', '#968d63'),  # 8 сизая степная
    ('#4f5230', '#7a7448', '#a59869'),  # 9 подсыхающая
    ('#575532', '#827a50', '#aa9d72'),  # 10 полусухая
    ('#5c5836', '#878057', '#b0a479'),  # 11 полусухая светлая
    ('#665c38', '#928458', '#bfad84'),  # 12 солома
    ('#5c5434', '#857a52', '#ab9c74'),  # 13 солома тёмная
    ('#6f6643', '#9c9064', '#c6b892'),  # 14 солома светлая
    ('#3d4b28', '#5a693c', '#808b58'),  # 15
]

ATLAS_SIZE = 256

# Пресеты (размеры в метрах). length: (медиана, мин, макс) — логнормально, или height: (мин, макс) — равномерно;
# colours: [(доля, [полосы])]; blades — число травинок по вариантам; edge/lobes — пятно (раздел 4 плана);
# segments — звеньев у травинки; lean/curl — наклон у основания и изгиб к кончику (рад) вместо blade_tilt;
# blunt — доля срезанных (скошенных) листьев; outward — листья клонятся от центра дерновины.
PRESETS = {
    'short_sward': dict(radius=0.9, lobes=2, flat=0.3, halo=(0.25, 1.6), budget=(1500, 1300), segments='auto', tiller_share=0.25,
                        length=(0.15, 0.06, 0.28), width=(0.0035, 0.006), width_power=2.5, lodged=0.12,
                        colours=[(0.75, [0, 1, 2, 3, 7, 15]), (0.15, [9]), (0.10, [12, 13])]),
    'short_sward_dry': dict(radius=0.9, lobes=2, flat=0.3, halo=(0.25, 1.6), budget=(1420, 1260), segments='auto', tiller_share=0.25,
                            length=(0.12, 0.05, 0.24), width=(0.0015, 0.003), width_power=1.5, lodged=0.20,
                            colours=[(0.55, [4, 5, 6, 8]), (0.23, [10, 11]), (0.22, [12, 13, 14])]),
    'tall_tuft': dict(radius=0.27, lobes=3, edge=0.35, blades=(130, 130), segments=5, tiller_share=0.3,
                      length=(0.28, 0.16, 0.45), width=(0.0015, 0.003), width_power=1.6, lodged=0.15, outward=0.7,
                      colours=[(0.7, [0, 2, 5, 7]), (0.18, [6, 9]), (0.12, [12, 13])], seed_stalks=7),
    'thatch': dict(radius=0.6, lobes=2, flat=0.5, halo=(0.35, 1.6), blades=(240, 240), segments=3, aligned=0.5),
    'mown_turf': dict(radius=0.9, lobes=2, flat=0.25, halo=(0.25, 1.6), budget=(1560,), height=(0.03, 0.08), width=(0.004, 0.009), segments=1,
                      lean=(0.1, 0.8), curl=(0.0, 0.3), dry=0.05, seed_stalks=0, normal_up=0.7, lift=0.0, blunt=0.5,
                      tiller_share=0.3, width_power=2.0, colours=[(1.0, [0, 1, 2, 3, 5, 7, 15])], dry_colours=[9, 12]),
}


def strip_uv(index):
    return (index + 0.5) / len(STRIPS)


def build_atlas(out_dir):
    """Атлас 256²: полосы цвета по ширине (U), от комля (V=0) к кончику (V=1); тонкие продольные жилки шумом.
    Нормали плоские (форму даёт геометрия). Маска NatureManufacture: G — AO к комлю (мягко), A — гладкость."""
    w = h = ATLAS_SIZE
    sw = w // len(STRIPS)
    rng = np.random.default_rng(7)
    albedo = np.zeros((h, w, 3), np.float32)
    v = np.linspace(1, 0, h)[:, None]  # строка 0 — верх картинки = кончик (V=1)
    for i, (base, mid, tip) in enumerate(STRIPS):
        b, m, t = (np.array(eg.hex_rgb(c), np.float32) for c in (base, mid, tip))
        col = np.where(v < 0.5, b + (m - b) * (v / 0.5), m + (t - m) * ((v - 0.5) / 0.5))
        x0 = i * sw
        veins = 1.0 + (rng.random((1, sw)) - 0.5) * 0.16
        noise = 1.0 + (rng.random((h, sw)) - 0.5) * 0.05
        albedo[:, x0:x0 + sw] = col[:, None, :] * veins[..., None] * noise[..., None]
    normal = np.zeros((h, w, 3), np.float32) + np.array([0.5, 0.5, 1.0], np.float32)
    mask = np.zeros((h, w, 4), np.float32)
    mask[..., 1] = np.clip(0.6 + 0.4 * np.sqrt(v), 0, 1)[:, 0:1].repeat(w, axis=1)
    mask[..., 3] = 0.3
    eg.save_png(albedo, os.path.join(out_dir, 'grass_atlas_albedo.png'))
    eg.save_png(normal, os.path.join(out_dir, 'grass_atlas_normal.png'), linear=True)
    eg.save_png(mask, os.path.join(out_dir, 'grass_atlas_mask.png'), linear=True)


def pick_colour(rng, colours):
    r = rng.random()
    for share, strips in colours:
        if r < share:
            return rng.choice(strips)
        r -= share
    return rng.choice(colours[-1][1])


def blade_tilt(rng, lodged):
    r = rng.random()
    if r < lodged:
        return math.radians(rng.uniform(55, 80))
    if r < lodged + 0.18:
        return math.radians(rng.uniform(30, 50))
    return math.radians(rng.uniform(5, 30))


STOPS = {1: (0.0, 1.0), 2: (0.0, 0.6, 1.0), 3: (0.0, 0.45, 0.8, 1.0), 4: (0.0, 0.35, 0.65, 0.87, 1.0),
         5: (0.0, 0.3, 0.55, 0.75, 0.9, 1.0)}


def blade(mb, normals, rng, root, length, width, width_power, tilt, yaw, strip, segments=None, droop=None, blunt=False,
          normal_up=0.7):
    """Травинка: segments звеньев (по умолчанию 3 у длинной и 2 у короткой), единственная вершина на кончике; ширина
    w0·(1 − t^p); наклон tilt, кончик клонится ещё на droop (по умолчанию 10–45° по длине), лёгкое скручивание.
    blunt — срезанный лист (скошенный газон): одно звено, тупой конец 75 % ширины, цвет до середины полосы (у среза нет
    светлого кончика). Нормали — normal_up вверх + грань, края завёрнуты на ±25°."""
    if blunt:
        stops = (0.0, 1.0)
    elif segments:
        stops = STOPS[segments if length >= 0.08 or segments <= 2 else segments - 1]
    else:
        stops = (0.0, 0.45, 0.8, 1.0) if length >= 0.12 else (0.0, 0.6, 1.0)
    if droop is None:
        droop = math.radians(rng.uniform(10, 45)) * min(1.0, length / 0.22)
    twist = math.radians(rng.uniform(0, 30))
    direction = Vector((math.cos(yaw), math.sin(yaw), 0))
    side0 = Vector((-direction.y, direction.x, 0))
    u = strip_uv(strip)
    du = 0.42 / len(STRIPS)
    v_end = 0.55 if blunt else 1.0
    pos = Vector(root)
    points = [pos.copy()]
    prev_t = 0.0
    for t in stops[1:]:
        angle = tilt + droop * ((prev_t + t) * 0.5) ** 1.5
        step = Vector((0, 0, math.cos(angle))) + direction * math.sin(angle)
        pos = pos + step.normalized() * (length * (t - prev_t))
        points.append(pos.copy())
        prev_t = t
    up = Vector((0, 0, 1))
    side_k = 1.0 - normal_up
    rows = []
    for i, (t, p) in enumerate(zip(stops, points)):
        tangent = (points[min(i + 1, len(points) - 1)] - points[max(i - 1, 0)]).normalized()
        side = side0.copy()
        side.rotate(Quaternion(tangent, twist * t))
        face = tangent.cross(side).normalized()
        if face.z < 0:
            face = -face
        h = p.z - root[2]
        c = (1.0, 1.0, 1.0, min(1.0, (max(h, 0.0) / 0.25) ** 1.5) * 0.9)
        last = i == len(stops) - 1
        if not last or blunt:
            half = width * 0.5 * ((1 - 0.25 * t) if blunt else (1 - t ** width_power))
            left = mb.vert(p - side * half)
            right = mb.vert(p + side * half)
            nl = face.copy()
            nl.rotate(Quaternion(tangent, math.radians(25)))
            nr = face.copy()
            nr.rotate(Quaternion(tangent, math.radians(-25)))
            rows.append(('pair', left, right, t * v_end, c, (up * normal_up + nl * side_k).normalized(),
                         (up * normal_up + nr * side_k).normalized()))
        else:
            rows.append(('tip', mb.vert(p), None, t, c, (up * (normal_up + 0.1) + face * (side_k - 0.1)).normalized(), None))
    for i in range(len(rows) - 1):
        _, a0, a1, t0, c0, n0l, n0r = rows[i]
        kind, b0, b1, t1, c1, n1l, n1r = rows[i + 1]
        if kind == 'tip':
            mb.face([a0, a1, b0], 0, [(u - du, t0), (u + du, t0), (u, t1)], None, [c0, c0, c1])
            normals.extend([n0l, n0r, n1l])
        else:
            mb.face([a0, a1, b1, b0], 0, [(u - du, t0), (u + du, t0), (u + du, t1), (u - du, t1)], None, [c0, c0, c1, c1])
            normals.extend([n0l, n0r, n1r, n1l])


def panicle_stalk(mb, normals, rng, root, height):
    """Соломина с узкой метёлкой: стебель — две полоски крест-накрест в 2 звена (подсыхающая полоса), в верхней
    четверти — 10–14 колосков: треугольники 8–12 мм под 15–35° к оси (светлая солома) — метёлка видна и с 3–4 м."""
    yaw = rng.uniform(0, math.tau)
    lean = rng.uniform(0.05, 0.25)
    bend = rng.uniform(0.05, 0.2)
    root = Vector(root)
    d0 = Vector((math.cos(yaw) * math.sin(lean), math.sin(yaw) * math.sin(lean), math.cos(lean)))
    d1 = Vector((math.cos(yaw) * math.sin(lean + bend), math.sin(yaw) * math.sin(lean + bend), math.cos(lean + bend)))
    mid = root + d0 * height * 0.55
    top = mid + d1 * height * 0.45
    n_up = Vector((0, 0, 1))
    u = strip_uv(9)
    du = 0.4 / len(STRIPS)

    def wind(p):
        return min(1.0, (max(p.z - root.z, 0.0) / 0.25) ** 1.5) * 0.9

    for k in range(2):
        a = yaw + k * math.pi / 2 + math.pi / 4
        side_dir = Vector((math.cos(a), math.sin(a), 0))
        pts = ((root, 0.0015, 0.0), (mid, 0.0012, 0.45), (top, 0.0008, 0.8))
        for (p0, w0, v0), (p1, w1, v1) in zip(pts[:-1], pts[1:]):
            idx = [mb.vert(p0 - side_dir * w0), mb.vert(p0 + side_dir * w0), mb.vert(p1 + side_dir * w1), mb.vert(p1 - side_dir * w1)]
            mb.face(idx, 0, [(u - du, v0), (u + du, v0), (u + du, v1), (u - du, v1)], None,
                    [(1, 1, 1, wind(p0))] * 2 + [(1, 1, 1, wind(p1))] * 2)
            normals.extend([n_up] * 4)
    axis = (top - mid).normalized()
    us = strip_uv(rng.choice([12, 14, 14]))
    count = rng.randint(10, 14)
    for k in range(count):
        t = 0.75 + 0.25 * (k + rng.random()) / count
        at = mid.lerp(top, (t - 0.55) / 0.45)
        out = axis.orthogonal()
        out.rotate(Quaternion(axis, rng.uniform(0, math.tau)))
        ang = math.radians(rng.uniform(15, 35))
        tip = at + (axis * math.cos(ang) + out * math.sin(ang)) * rng.uniform(0.008, 0.012)
        side = axis.cross(out).normalized() * 0.0012
        a = wind(at)
        mb.face([mb.vert(at - side), mb.vert(at + side), mb.vert(tip)], 0, [(us - du, 0.7), (us + du, 0.7), (us, 1.0)], None,
                [(1, 1, 1, a)] * 3)
        normals.extend([(n_up * 0.8 + out * 0.2).normalized()] * 3)


def smooth(a, b, v):
    t = min(1.0, max(0.0, (v - a) / (b - a)))
    return t * t * (3 - 2 * t)


def make_blob(rng, radius, lobes):
    """Пятно из лопастей (раздел 4 плана): центральная 0.62 R и 2..lobes боковых; blob > 0 внутри, 1 в центре лопасти,
    волнистость ±0.16."""
    parts = [(0.0, 0.0, 0.62 * radius)]
    for _ in range(rng.randint(2, lobes)):
        a = rng.uniform(0, math.tau)
        d = rng.uniform(0.30, 0.55) * radius
        parts.append((d * math.cos(a), d * math.sin(a), rng.uniform(0.32, 0.50) * radius))
    w = [rng.uniform(0, math.tau) for _ in range(4)]

    def blob(x, y):
        f = max(1.0 - math.hypot(x - cx, y - cy) / r for cx, cy, r in parts)
        return f + 0.10 * math.sin(11 * x + w[0]) * math.sin(9 * y + w[1]) + 0.06 * math.sin(23 * x + w[2]) * math.sin(19 * y + w[3])
    extent = max(math.hypot(cx, cy) + r for cx, cy, r in parts) * 1.25
    return blob, extent


def make_flat_blob(rng, radius, lobes=2, harmonic=0.14, ripple=1.0):
    """Ковровое пятно: 1–2 лопасти, край у каждой неровный (гармоники 2, 3, 5 по углу ±harmonic), лёгкая рябь. blob —
    1 − d/r: 0 на краю, 1 в центре лопасти; плотность — smooth(−0.06, flat, blob): ровная внутри, спад у края."""
    parts = []
    if lobes <= 1:
        parts.append((0.0, 0.0, radius))
    else:
        parts.append((0.0, 0.0, 0.84 * radius))
        for _ in range(lobes - 1):
            a = rng.uniform(0, math.tau)
            d = rng.uniform(0.18, 0.32) * radius
            parts.append((d * math.cos(a), d * math.sin(a), rng.uniform(0.6, 0.72) * radius))
    parts = [(cx, cy, r, [rng.uniform(0, math.tau) for _ in range(3)]) for cx, cy, r in parts]
    w = [rng.uniform(0, math.tau) for _ in range(4)]
    k = 1.0 / radius

    def blob(x, y):
        best = -9.0
        for cx, cy, r, ph in parts:
            dx, dy = x - cx, y - cy
            th = math.atan2(dy, dx)
            rr = r * (1 + harmonic * (0.55 * math.sin(2 * th + ph[0]) + 0.3 * math.sin(3 * th + ph[1]) + 0.15 * math.sin(5 * th + ph[2])))
            best = max(best, 1.0 - math.hypot(dx, dy) / rr)
        return best + ripple * (0.05 * math.sin(7 * k * x + w[0]) * math.sin(6 * k * y + w[1]) +
                                0.03 * math.sin(15 * k * x + w[2]) * math.sin(13 * k * y + w[3]))
    extent = max(math.hypot(cx, cy) + r for cx, cy, r, _ in parts) * (1 + harmonic) * 1.12
    return blob, extent


def blob_point(rng, blob, extent, edge, power=1.6, tries=4000, lo=-0.12):
    """Случайная точка пятна с плотностью smooth(−0.12, edge, blob)^power; возвращает (x, y, inner)."""
    for _ in range(tries):
        x, y = rng.uniform(-extent, extent), rng.uniform(-extent, extent)
        inner = smooth(lo, edge, blob(x, y))
        if inner > 0.0 and rng.random() <= inner ** power:
            return x, y, inner
    return 0.0, 0.0, 1.0


def patch_sampler(rng, p):
    """Точки пятна пресета: ковровое (flat — ширина спада у края) или лопастное (раздел 4 плана)."""
    if p.get('flat'):
        blob, extent = make_flat_blob(rng, p['radius'], p['lobes'])
        halo = p.get('halo')

        def sample(edge_k=1.0, power=1.0):
            # доля травинок — в редком ореоле (пятно в halo[1] раз больше, мягкий край, ниже ростом): где соседние
            # экземпляры легли реже, ореолы закрывают проплешину, и край пятна на редкой расстановке не читается
            if halo and rng.random() < halo[0]:
                sc = halo[1]
                x, y, inner = blob_point(rng, lambda x, y: blob(x / sc, y / sc), extent * sc, 0.5, power, lo=-0.06)
                return x, y, inner * 0.6
            return blob_point(rng, blob, extent, p['flat'] * edge_k, power, lo=-0.06)
        return blob, extent, sample
    blob, extent = make_blob(rng, p['radius'], p['lobes'])
    return blob, extent, lambda edge_k=1.0, power=1.6: blob_point(rng, blob, extent, p['edge'] * edge_k, power)


def build_patch(name, p, seed, variant=0):
    rng = random.Random(seed)
    mb = eg.MeshBuilder()
    normals = []
    blob, extent, sample = patch_sampler(rng, p)
    budget = p['budget'][min(variant, len(p['budget']) - 1)] if 'budget' in p else 0
    target = p['blades'][min(variant, len(p['blades']) - 1)] if 'blades' in p else 10 ** 6
    roots = []
    blades = attempts = 0
    while blades < target and attempts < 200000 and (not budget or mb.triangle_count < budget - 6):
        attempts += 1
        x, y, inner = sample()
        q = Vector((x, y, p.get('lift', 0.0) * rng.random()))
        # шаг не меньше 1.5 см (как синий шум) — по недавно поставленным
        if any((q - o).length < 0.015 for o in roots[-40:]):
            continue
        roots.append(q)
        tillers = rng.randint(3, 4) if rng.random() < p['tiller_share'] else 1
        tillers = min(tillers, target - blades)
        if budget:
            tillers = max(1, min(tillers, (budget - mb.triangle_count) // 5))
        if p.get('outward'):
            base_yaw = math.atan2(y, x) + rng.uniform(-1.0, 1.0) * (1.0 - p['outward']) * math.pi
        else:
            base_yaw = rng.uniform(0, math.tau)
        for k in range(tillers):
            offset = Vector((rng.uniform(-0.008, 0.008), rng.uniform(-0.008, 0.008), 0)) if k else Vector()
            edge_scale = 0.45 + 0.55 * inner ** 0.7  # к краю пятна ниже
            if 'height' in p:
                length = rng.uniform(*p['height']) * edge_scale
            else:
                median, lo, hi = p['length']
                length = min(hi, max(lo, median * math.exp(rng.gauss(0, 0.35)))) * edge_scale
            yaw = base_yaw + (math.radians(rng.uniform(-60, 60)) if k else 0)
            if 'lean' in p:
                tilt = rng.uniform(*p['lean'])
                droop = rng.uniform(*p['curl'])
            else:
                tilt = blade_tilt(rng, p['lodged'])
                droop = None
                if p.get('outward'):
                    tilt = max(tilt, math.radians(10) + 0.6 * math.hypot(x, y) / p['radius'])
            if p.get('dry') and rng.random() < p['dry']:
                strip = rng.choice(p['dry_colours'])
            else:
                strip = pick_colour(rng, p['colours'])
            segments = p.get('segments')
            if segments == 'auto':
                segments = 3 if length >= 0.17 else 2
            blade(mb, normals, rng, tuple(q + offset), length, rng.uniform(*p['width']), p['width_power'], tilt, yaw, strip,
                  segments=segments, droop=droop, blunt=rng.random() < p.get('blunt', 0.0),
                  normal_up=p.get('normal_up', 0.7))
            blades += 1
    if p.get('seed_stalks'):
        hi = p['length'][2]
        for i in range(p['seed_stalks']):
            x, y, inner = sample(0.5, 3.0)
            panicle_stalk(mb, normals, rng, (x * 0.6, y * 0.6, 0.0), rng.uniform(hi * 1.1, hi * 1.5))
    obj = mb.to_object(name, [eg.emission_material(name + '_mat', lambda nt: nt.nodes.new('ShaderNodeRGB').outputs[0])])
    eg.set_custom_normals(obj, normals)
    return obj, mb.triangle_count


def straw(mb, normals, rng, centre, strip, scale=1.0, yaw=None):
    """Лежащая соломина: 2 звена с изломом 15–60° на 35–65 % длины, конец тупой (40–70 % ширины), 0–15° над землёй."""
    length = rng.uniform(0.08, 0.30) * scale
    width = rng.uniform(0.002, 0.005)
    a = rng.uniform(0, math.tau) if yaw is None else yaw
    d = Vector((math.cos(a), math.sin(a), 0))
    knee = rng.uniform(0.35, 0.65)
    kink = math.radians(rng.uniform(15, 60)) * rng.choice([-1, 1])
    lift = math.radians(rng.uniform(0, 15))
    p0 = centre + Vector((0, 0, rng.uniform(0.0, 0.03)))
    d1 = (d * math.cos(lift) + Vector((0, 0, math.sin(lift)))).normalized()
    p1 = p0 + d1 * length * knee
    d2 = d1.copy()
    d2.rotate(Quaternion(Vector((0, 0, 1)), kink))
    p2 = p1 + d2 * length * (1 - knee)
    u = strip_uv(strip)
    du = 0.4 / len(STRIPS)
    end = rng.uniform(0.4, 0.7)
    n = (Vector((0, 0, 1)) * 0.85 + Vector((0.1, 0.1, 0))).normalized()
    prev = None
    for p, wf, t, seg_dir in ((p0, 1.0, 0.0, d1), (p1, (1 + end) / 2, 0.5, d1), (p2, end, 1.0, d2)):
        side = Vector((-seg_dir.y, seg_dir.x, 0)) * width * wf * 0.5
        pair = (mb.vert(p - side), mb.vert(p + side), t)
        if prev:
            mb.face([prev[0], prev[1], pair[1], pair[0]], 0, [(u - du, prev[2]), (u + du, prev[2]), (u + du, t), (u - du, t)],
                    None, [(1, 1, 1, 0)] * 4)
            normals.extend([n] * 4)
        prev = pair


def build_thatch(name, p, seed, variant=0):
    """Ветошь в ковровом пятне ~1.2 м (ровно внутри — без «гнезда» с густым центром): 72 % лежащих соломин (к краю
    короче), две трети из них прибиты снегом в одну сторону (±35°), 16 % сухих пеньков стеблей, 12 % коротких
    подсыхающих побегов (оливковые) — ветошь не бывает совсем мёртвой."""
    rng = random.Random(seed)
    mb = eg.MeshBuilder()
    normals = []
    blob, extent, sample = patch_sampler(rng, p)
    total = p['blades'][min(variant, len(p['blades']) - 1)]
    lay = rng.uniform(0, math.tau)
    u = strip_uv(13)
    du = 0.4 / len(STRIPS)
    for i in range(total):
        x, y, inner = sample()
        base = Vector((x, y, 0))
        roll = rng.random()
        if roll < 0.72:
            r = rng.random()
            yaw = lay + rng.uniform(-0.6, 0.6) if rng.random() < p.get('aligned', 0.0) else None
            straw(mb, normals, rng, base, 12 if r < 0.4 else 13 if r < 0.75 else 14, 0.55 + 0.45 * inner, yaw)
        elif roll < 0.88:
            h = rng.uniform(0.04, 0.09) * (0.6 + 0.4 * inner)
            a = rng.uniform(0, math.tau)
            tilt = Vector((rng.uniform(-0.25, 0.25), rng.uniform(-0.25, 0.25), 1)).normalized()
            side = tilt.cross(Vector((math.cos(a), math.sin(a), 0))).normalized() * rng.uniform(0.0008, 0.0015)
            top = base + tilt * h
            at = (h / 0.09) * 0.15
            idx = [mb.vert(base - side), mb.vert(base + side), mb.vert(top + side), mb.vert(top - side)]
            mb.face(idx, 0, [(u - du, 0), (u + du, 0), (u + du, 0.5), (u - du, 0.5)], None,
                    [(1, 1, 1, 0), (1, 1, 1, 0), (1, 1, 1, at), (1, 1, 1, at)])
            normals.extend([Vector((0, 0, 1))] * 4)
        else:
            blade(mb, normals, rng, (x, y, 0.0), rng.uniform(0.05, 0.11) * (0.6 + 0.4 * inner), rng.uniform(0.002, 0.004), 2.0,
                  math.radians(rng.uniform(10, 40)), rng.uniform(0, math.tau), rng.choice([4, 6, 9]), segments=p['segments'])
    obj = mb.to_object(name, [eg.emission_material(name + '_mat', lambda nt: nt.nodes.new('ShaderNodeRGB').outputs[0])])
    eg.set_custom_normals(obj, normals)
    return obj, mb.triangle_count


# Материалы (NM_Foliage NR, роль grass) — дизайн, раздел 4.2
BASE_MATERIAL = dict(role='grass', albedo='grass_atlas_albedo.png', normal='grass_atlas_normal.png', mask='grass_atlas_mask.png',
                     alphaCutoff=0.05, alphaDrag=1.0, initialBend=1.0, normalScale=0.5, healthyColor=[1.0, 1.0, 1.0],
                     rotorWashClear=0.0, invertBackfaceNormal=0.0, aoMax=1.0, smoothnessMin=0.0, distanceControl=1.0)
MATERIALS = {
    'short_sward': dict(stiffness=1.0, drag=0.8, shiverDrag=0.06, shiverDirectionality=0.6, specular=0.25, smoothnessMax=0.35,
                        aoMin=0.55, dryColor=[0.93, 0.88, 0.70], colorNoiseSpread=0.35, puddleSink=0.20, sizeVariation=0.12,
                        scaleFade=[100000, 8]),
    'short_sward_dry': dict(stiffness=1.0, drag=0.7, shiverDrag=0.06, shiverDirectionality=0.6, specular=0.22, smoothnessMax=0.30,
                            aoMin=0.55, dryColor=[0.95, 0.90, 0.72], colorNoiseSpread=0.5, puddleSink=0.18, sizeVariation=0.12,
                            scaleFade=[100000, 8]),
    'thatch': dict(stiffness=1.0, drag=0.3, shiverDrag=0.02, shiverDirectionality=0.5, specular=0.15, smoothnessMax=0.20,
                   aoMin=0.6, dryColor=[0.90, 0.88, 0.82], colorNoiseSpread=0.5, puddleSink=0.05, sizeVariation=0.1,
                   scaleFade=[100000, 6], seasonKeep=2.0),
    'tall_tuft': dict(stiffness=1.0, drag=0.7, shiverDrag=0.06, shiverDirectionality=0.6, specular=0.25, smoothnessMax=0.3,
                      aoMin=0.55, dryColor=[0.93, 0.88, 0.70], colorNoiseSpread=0.4, puddleSink=0.4, sizeVariation=0.35,
                      scaleFade=[100000, 15]),
    'mown_turf': dict(stiffness=1.0, drag=0.8, shiverDrag=0.05, shiverDirectionality=0.6, specular=0.25, smoothnessMax=0.35,
                      aoMin=0.55, dryColor=[0.93, 0.88, 0.70], colorNoiseSpread=0.35, puddleSink=0.08, sizeVariation=0.12,
                      scaleFade=[100000, 8]),
}
NATURE_RENDERER = {
    'short_sward': dict(renderDistance=50.0, shadows=False, densityInDistance=0.25, densityFalloff=[0.008, 0.003]),
    'short_sward_dry': dict(renderDistance=35.0, shadows=False, densityInDistance=0.25, densityFalloff=[0.008, 0.003]),
    'thatch': dict(renderDistance=25.0, shadows=False, densityInDistance=0.3),
    'tall_tuft': dict(renderDistance=100.0, shadows=True),
    'mown_turf': dict(renderDistance=50.0, shadows=False, densityInDistance=0.25, densityFalloff=[0.008, 0.003]),
}


def main():
    args = eg.script_args()
    out_dir = args[0]
    presets = args[1:] or ['short_sward', 'short_sward_dry', 'thatch', 'tall_tuft', 'mown_turf']
    os.makedirs(out_dir, exist_ok=True)
    report_path = os.path.join(out_dir, 'grass_report.json')
    report = {}
    if os.path.exists(report_path):
        with open(report_path, encoding='utf-8') as f:
            report = json.load(f)
    for name in presets:
        p = PRESETS[name]
        for variant in range(len(p.get('budget', p.get('blades')))):
            asset = f'grass_{name}_{variant + 1:02d}'
            folder = os.path.join(out_dir, asset)
            os.makedirs(folder, exist_ok=True)
            eg.reset_scene()
            build_atlas(folder)
            eg.reset_scene()
            seed = zlib.crc32(asset.encode())
            obj, tris = (build_thatch if name == 'thatch' else build_patch)(asset, p, seed, variant)
            dims = obj.dimensions
            eg.export_fbx([obj], os.path.join(folder, asset + '.fbx'))
            material = dict(BASE_MATERIAL, id='grass_' + name, **MATERIALS[name])
            manifest = eg.write_manifest(folder, asset, 'detail', 'Grass',
                                         [dict(fbx=asset + '.fbx', screen=0.0, submeshes=[material['id']],
                                               castShadows=name == 'tall_tuft')], [material])
            manifest['nr'] = NATURE_RENDERER[name]
            with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
                json.dump(manifest, f, indent=1)
            report[asset] = dict(triangles=tris, size=[round(dims.x, 3), round(dims.y, 3), round(dims.z, 3)])
    with open(report_path, 'w', encoding='utf-8') as f:
        json.dump(report, f, indent=1)
    print('GRASS_OK', json.dumps(report))


main()
