# Подлесок для леса (Tools/TreeGen; дизайн леса, этап 3–4): то, что на снимках лесов Харьковщины стоит между большими
# стволами «частоколом» и чего не было у нас (лес читался парком: голые стволы на ровной подстилке):
#  - understory_pole_01..05 — сухой тонкомер: усохшие в тени деревца 2.5–7 м, Ø 3–8 см, с наклоном, изогнутые, с
#    короткими обломанными сучками, у половины обломана верхушка; серая мёртвая кора (oak_dead_branch);
#  - understory_sapling_01..04 — живой подрост (молодые дубки, клёны, липки) 1.6–4.5 м: тонкий ствол, редкие ветви, на их
#    концах — веточки с листьями из атласа дуба (oak_leaves). Один материал (листва, NM_Foliage): деталь Nature Renderer
#    обязана быть одним материалом, а детали из двух частей сажались бы порознь; ствол и ветви — на полосе коры, дорисованной
#    в свободный верх ячейки 0 атласа (understory_leaves_*: альфа 1, цвет коры ветвей, не зелёная — поэтому шейдер поры года
#    оставляет её и голому деревцу: осенью листва цветная или опала, зимой — голые прутья);
#  - understory_bush_01..03 — куст подлеска (лещина, бересклет, жимолость) 1.4–3 м: 6–12 стеблей из одного места дугой
#    наружу, ветви в верхней половине, листья вдоль стеблей и на концах — тот же материал, что у подроста;
#  - understory_twigs_01..03 — куст сухих прутьев (сухие побеги лещины, малинник): 6–14 тонких стеблей из одного места,
#    дугой наружу, с мелкими веточками, 0.6–1.8 м;
#  - understory_brush_01..02 — куча хвороста после рубки: 24–40 палок и веток 0.6–2.6 м внахлёст, 2.5 x 1.6 м, до 0.8 м.
# Всё — детали Nature Renderer (VegetationImporter, type detail), по одному материалу; их сажает GroundDressing под
# деревьями леса. Ветер — слабый (цвет вершин A: 0 у комля, растёт к концам; у сухого — вдвое меньше).
# Текстуры — из _tex генератора дуба (oak_gen.py): oak_dead_branch_*, oak_branch_*, oak_leaves_* (своё, процедурное, и
# фото-скан Poly Haven CC0 в oak_dead_bark — здесь не нужен).
# Запуск (Blender 5.2, фоном):
#   blender.exe -b --factory-startup --python understory_gen.py -- --tex <_tex генератора дуба> --out <папка> [--assets ...]
import argparse
import json
import math
import os
import random
import shutil
import sys

import bpy
import numpy as np
from mathutils import Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402
import tree_common as tc  # noqa: E402

GROUP = 'Understory'
BRANCH_TILE = 0.3          # м: плитка коры ветвей oak_branch / oak_dead_branch
UP = Vector((0.0, 0.0, 1.0))
# полоса коры в атласе листвы (UV, v снизу): свободный верх ячейки 0 (альфа-рамка ячейки кончается на v 0.862)
BARK_RECT = (0.03, 0.88, 0.47, 0.985)
DEAD_WIND = 0.5


def tapered(n, r0, r1, power=1.0):
    return [r0 + (r1 - r0) * (i / max(n - 1, 1)) ** power for i in range(n)]


def rotate(v, axis, angle):
    v = Vector(v)
    v.rotate(Quaternion(Vector(axis).normalized(), angle))
    return v


def side_direction(d, rng, angle):
    """Направление сучка от оси d: наружу под углом angle, вокруг оси — случайно."""
    axis = d.orthogonal().normalized()
    axis.rotate(Quaternion(d, rng.uniform(0, math.tau)))
    return rotate(d, axis, angle).normalized()


def set_wind(branch, w0, w1):
    branch.weight0 = w0
    branch.weight1 = w1


# ------------------------------------------------------------------ сухой тонкомер

def build_pole(rng, H, r0, twigs, broken):
    lean = math.radians(rng.uniform(2, 12))
    az = rng.uniform(0, math.tau)
    d0 = Vector((math.sin(lean) * math.cos(az), math.sin(lean) * math.sin(az), math.cos(lean)))
    segs = max(6, int(H / 0.3))
    pts = tc.grow_curve((0, 0, -0.05), d0, H + 0.05, segs, rng, gravity=0.0, wobble=0.05, up_curve=0.01)
    if broken:
        keep = max(4, int(len(pts) * rng.uniform(0.55, 0.85)))
        pts = pts[:keep]
    tip = r0 * (0.45 if broken else 0.12)
    stem = tc.Branch(pts, tapered(len(pts), r0, tip, 0.8), 0, alive=False)
    set_wind(stem, 0.0, 0.12 * DEAD_WIND)
    out = [stem]
    for _ in range(twigs):
        t = rng.uniform(0.2, 0.95)
        p, r, d = stem.point_at(t)
        length = rng.uniform(0.12, 0.25) if rng.random() < 0.45 else rng.uniform(0.3, 0.9) * (1.15 - t * 0.5)
        dd = side_direction(d, rng, math.radians(rng.uniform(40, 75)))
        n = max(2, int(length / 0.12))
        tp = tc.grow_curve(p, dd, length, n, rng, gravity=0.05, wobble=0.12)
        rr = min(r * 0.45, 0.012)
        tw = tc.Branch(tp, tapered(len(tp), rr, rr * 0.35), 1, stem, alive=False)
        set_wind(tw, stem.weight1 * t, 0.3 * DEAD_WIND)
        out.append(tw)
    return out


# ------------------------------------------------------------------ живой подрост

def build_sapling(rng, H, r0, n_branches):
    lean = math.radians(rng.uniform(1, 8))
    az = rng.uniform(0, math.tau)
    d0 = Vector((math.sin(lean) * math.cos(az), math.sin(lean) * math.sin(az), math.cos(lean)))
    segs = max(6, int(H / 0.25))
    pts = tc.grow_curve((0, 0, -0.04), d0, H + 0.04, segs, rng, wobble=0.07, up_curve=0.03)
    stem = tc.Branch(pts, tapered(len(pts), r0, 0.004, 0.9), 0)
    set_wind(stem, 0.0, 0.35)
    branches = [stem]
    tips = [(stem.points[-1], (stem.points[-1] - stem.points[-2]).normalized(), 0.35)]
    for tt in (0.6, 0.78, 0.9):
        q, _, dq = stem.point_at(tt)
        tips.append((q, side_direction(dq, rng, math.radians(rng.uniform(40, 70))), 0.4))
    for k in range(n_branches):
        t = 0.35 + 0.6 * (k + rng.uniform(0, 0.8)) / n_branches
        p, r, d = stem.point_at(min(t, 0.97))
        length = rng.uniform(0.35, 1.0) * (1.25 - t * 0.6) * min(1.0, H / 3.0)
        dd = side_direction(d, rng, math.radians(rng.uniform(35, 65)))
        n = max(2, int(length / 0.12))
        bp = tc.grow_curve(p, dd, length, n, rng, gravity=0.12, wobble=0.1, up_curve=0.06)
        rr = min(r * 0.55, 0.012)
        br = tc.Branch(bp, tapered(len(bp), rr, 0.003), 1, stem)
        set_wind(br, 0.35 * t, 0.7)
        branches.append(br)
        tips.append((br.points[-1], (br.points[-1] - br.points[-2]).normalized(), 0.7))
        # листья и вдоль ветви, не только на конце (иначе деревце — «леденец»)
        for tt in (0.45, 0.72):
            if length > 0.4:
                q, _, dq = br.point_at(tt)
                tips.append((q, (dq + side_direction(dq, rng, math.radians(50)) * 0.6).normalized(), 0.55))
        if length > 0.55 and rng.random() < 0.6:
            q, rq, dq = br.point_at(rng.uniform(0.4, 0.7))
            l2 = length * rng.uniform(0.3, 0.5)
            sp = tc.grow_curve(q, side_direction(dq, rng, math.radians(40)), l2, 2, rng, gravity=0.1, wobble=0.1)
            sub = tc.Branch(sp, [rq * 0.6, 0.0025, 0.002][:len(sp)], 2, br)
            set_wind(sub, 0.5, 0.8)
            branches.append(sub)
            tips.append((sub.points[-1], (sub.points[-1] - sub.points[-2]).normalized(), 0.8))
    return branches, tips


def leaf_cards(mb, material, tips, cells, rng, size_range):
    """Веточки с листьями атласа на концах ветвей: карточка — по ходу ветви, лицом наружу-вверх; 2 треугольника,
    дважды (обе стороны рисует материал листвы)."""
    tris = 0
    for p, d, w in tips:
        for _ in range(rng.choice([1, 1, 2])):
            cell = cells[rng.choice([0, 1, 2, 3])]
            u0, v0, u1, v1 = cell['uvRect']
            ab = cell['alphaBox']
            size = rng.uniform(*size_range)
            ww, hh = cell['worldSize']
            scale = size / max(ww, hh)
            w_m, h_m = ww * scale, hh * scale
            fwd = (d + UP * 0.6 + Vector((rng.uniform(-0.3, 0.3), rng.uniform(-0.3, 0.3), 0))).normalized()
            side = fwd.cross(UP)
            if side.length < 1e-4:
                side = Vector((1, 0, 0))
            side = rotate(side.normalized(), fwd, rng.uniform(-0.5, 0.5))
            base = p - fwd * h_m * 0.08
            a = base - side * w_m * 0.5
            b = base + side * w_m * 0.5
            c = b + fwd * h_m
            e = a + fwd * h_m
            uv = [(ab[0], ab[1]), (ab[2], ab[1]), (ab[2], ab[3]), (ab[0], ab[3])]
            col = [(w, w, w, w), (w, w, w, w), (1.0, 1.0, 1.0, 1.0), (1.0, 1.0, 1.0, 1.0)]
            ids = [mb.vert(a), mb.vert(b), mb.vert(c), mb.vert(e)]
            mb.face(ids, material, uv, None, col)
            tris += 2
    return tris


def sweep_rect(mb, branch, material, sides, rect, metres_v, rng, cap=True):
    """Как tc.sweep, но UV — в прямоугольник атласа rect: по окружности на всю его ширину, вдоль — туда-обратно по его
    высоте (кора не тайлится внутри атласа)."""
    pts = branch.points
    n = len(pts)
    if n < 2:
        return
    frames = []
    prev = None
    for i in range(n):
        if i == 0:
            t = (pts[1] - pts[0]).normalized()
        elif i == n - 1:
            t = (pts[-1] - pts[-2]).normalized()
        else:
            t = ((pts[i + 1] - pts[i]).normalized() + (pts[i] - pts[i - 1]).normalized()).normalized()
        nx = t.orthogonal().normalized() if prev is None else (prev - t * prev.dot(t)).normalized()
        prev = nx
        frames.append((t, nx, t.cross(nx).normalized()))
    s = 0.0
    rings = []
    for i in range(n):
        if i > 0:
            s += (pts[i] - pts[i - 1]).length
        ph = (s / metres_v) % 2.0
        v = rect[1] + (rect[3] - rect[1]) * (ph if ph <= 1.0 else 2.0 - ph)
        t, nx, ny = frames[i]
        r = max(branch.radii[i], 0.0015)
        w = branch.weight0 + (branch.weight1 - branch.weight0) * (i / (n - 1))
        ring = []
        for k in range(sides + 1):
            a = k / sides * math.tau
            off = nx * math.cos(a) + ny * math.sin(a)
            u = rect[0] + (rect[2] - rect[0]) * (1 - abs(2 * k / sides - 1))
            ring.append((mb.vert(pts[i] + off * r), (u, v), w))
        rings.append(ring)
    for i in range(n - 1):
        for k in range(sides):
            a, b, c, d = rings[i][k], rings[i][k + 1], rings[i + 1][k + 1], rings[i + 1][k]
            mb.face([a[0], b[0], c[0], d[0]], material, [a[1], b[1], c[1], d[1]], None,
                    [(a[2],) * 3 + (a[2],), (b[2],) * 3 + (b[2],), (c[2],) * 3 + (c[2],), (d[2],) * 3 + (d[2],)])
    if cap:
        last = rings[-1]
        tip = mb.vert(pts[-1] + frames[-1][0] * branch.radii[-1] * 0.5)
        for k in range(sides):
            a, b = last[k], last[k + 1]
            mb.face([a[0], b[0], tip], material, [a[1], b[1], a[1]], None, [(a[2],) * 4, (b[2],) * 4, (a[2],) * 4])


# ------------------------------------------------------------------ сухие прутья и хворост

def build_bush(rng, n_stems, height):
    """Куст: стебли из одного места дугой наружу, ветви в верхней половине; концы — для карточек листвы."""
    branches, tips = [], []
    for _ in range(n_stems):
        tilt = math.radians(rng.uniform(6, 38))
        az = rng.uniform(0, math.tau)
        d0 = Vector((math.sin(tilt) * math.cos(az), math.sin(tilt) * math.sin(az), math.cos(tilt)))
        length = height * rng.uniform(0.6, 1.1)
        n = max(4, int(length / 0.3))
        base = Vector((rng.uniform(-0.12, 0.12), rng.uniform(-0.12, 0.12), -0.04))
        pts = tc.grow_curve(base, d0, length, n, rng, gravity=rng.uniform(0.08, 0.3), wobble=0.08, up_curve=0.02)
        r0 = rng.uniform(0.008, 0.016)
        stem = tc.Branch(pts, tapered(len(pts), r0, 0.003), 0)
        set_wind(stem, 0.0, 0.5)
        branches.append(stem)
        tips.append((stem.points[-1], (stem.points[-1] - stem.points[-2]).normalized(), 0.55))
        for tt in (0.45, 0.62, 0.8):
            q, _, dq = stem.point_at(tt)
            tips.append((q, side_direction(dq, rng, math.radians(rng.uniform(40, 70))), 0.45))
        for _ in range(rng.randint(1, 3)):
            q, rq, dq = stem.point_at(rng.uniform(0.45, 0.85))
            l2 = length * rng.uniform(0.2, 0.4)
            bp = tc.grow_curve(q, side_direction(dq, rng, math.radians(rng.uniform(35, 60))), l2, 3, rng, gravity=0.12,
                               wobble=0.1)
            br = tc.Branch(bp, tapered(len(bp), min(rq * 0.6, 0.008), 0.0025), 1, stem)
            set_wind(br, 0.4, 0.7)
            branches.append(br)
            tips.append((br.points[-1], (br.points[-1] - br.points[-2]).normalized(), 0.7))
    return branches, tips


def build_twigs(rng, n_stems, height):
    out = []
    for _ in range(n_stems):
        tilt = math.radians(rng.uniform(8, 42))
        az = rng.uniform(0, math.tau)
        d0 = Vector((math.sin(tilt) * math.cos(az), math.sin(tilt) * math.sin(az), math.cos(tilt)))
        length = height * rng.uniform(0.55, 1.15)
        n = max(4, int(length / 0.12))
        base = Vector((rng.uniform(-0.08, 0.08), rng.uniform(-0.08, 0.08), -0.03))
        pts = tc.grow_curve(base, d0, length, n, rng, gravity=rng.uniform(0.1, 0.45), wobble=0.12)
        r0 = rng.uniform(0.005, 0.011)
        stem = tc.Branch(pts, tapered(len(pts), r0, 0.0018), 0, alive=False)
        set_wind(stem, 0.0, 0.5 * DEAD_WIND)
        out.append(stem)
        for _ in range(rng.randint(1, 4)):
            p, r, d = stem.point_at(rng.uniform(0.3, 0.85))
            l2 = length * rng.uniform(0.12, 0.3)
            tp = tc.grow_curve(p, side_direction(d, rng, math.radians(rng.uniform(30, 60))), l2, 3, rng, gravity=0.1,
                               wobble=0.15)
            tw = tc.Branch(tp, tapered(len(tp), r * 0.6, 0.0012), 1, stem, alive=False)
            set_wind(tw, 0.3 * DEAD_WIND, 0.6 * DEAD_WIND)
            out.append(tw)
    return out


def build_brush(rng, n_sticks, size=(2.5, 1.6), height=0.8):
    out = []
    a, b = size[0] * 0.5, size[1] * 0.5
    for _ in range(n_sticks):
        while True:
            x, y = rng.uniform(-a, a), rng.uniform(-b, b)
            q = (x / a) ** 2 + (y / b) ** 2
            if q <= 1.0:
                break
        z = height * (1 - q) * rng.uniform(0.25, 1.0) + 0.03
        length = rng.uniform(0.6, 2.6)
        heading = rng.uniform(0, math.tau)
        # палки лежат вдоль кучи чаще, чем поперёк
        heading = heading * 0.4 if rng.random() < 0.6 else heading
        tilt = math.radians(rng.uniform(-12, 12))
        d = Vector((math.cos(heading) * math.cos(tilt), math.sin(heading) * math.cos(tilt), math.sin(tilt)))
        start = Vector((x, y, z)) - d * length * 0.5
        n = max(3, int(length / 0.3))
        pts = tc.grow_curve(start, d, length, n, rng, wobble=0.07)
        pts = [Vector((p.x, p.y, min(max(p.z, 0.02), height * 1.1))) for p in pts]
        r0 = rng.uniform(0.012, 0.05)
        st = tc.Branch(pts, tapered(len(pts), r0, r0 * rng.uniform(0.3, 0.6)), 0, alive=False)
        set_wind(st, 0.0, 0.0)
        out.append(st)
        for _ in range(rng.randint(0, 3)):
            p, r, dd = st.point_at(rng.uniform(0.2, 0.9))
            l2 = rng.uniform(0.2, 0.7)
            tp = tc.grow_curve(p, side_direction(dd, rng, math.radians(rng.uniform(30, 60))), l2, 2, rng, wobble=0.1)
            tp = [Vector((v.x, v.y, max(v.z, 0.02))) for v in tp]
            tw = tc.Branch(tp, tapered(len(tp), min(r * 0.45, 0.012), 0.002), 1, st, alive=False)
            set_wind(tw, 0.0, 0.0)
            out.append(tw)
    return out


# ------------------------------------------------------------------ текстуры и экспорт

def make_leaf_atlas(tex, out_prefix):
    """Атлас листвы дуба + полоса коры (BARK_RECT) в свободном верху ячейки 0: цвет коры ветвей, альфа 1, нормаль плоская."""
    def load(name, data):
        img = bpy.data.images.load(os.path.join(tex, name), check_existing=False)
        if data:
            img.colorspace_settings.name = 'Non-Color'
        w, h = img.size
        px = np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1].copy()
        bpy.data.images.remove(img)
        return px

    albedo = load('oak_leaves_albedo.png', True)
    normal = load('oak_leaves_normal.png', True)
    mask = load('oak_leaves_mask.png', True)
    bark = load('oak_branch_albedo.png', True)
    h, w = albedo.shape[:2]
    x0, x1 = int(BARK_RECT[0] * w), int(BARK_RECT[2] * w)
    y0, y1 = int((1 - BARK_RECT[3]) * h), int((1 - BARK_RECT[1]) * h)
    pad = 6
    bh, bw = bark.shape[:2]
    patch = np.array(bark[:y1 - y0 + 2 * pad, :x1 - x0 + 2 * pad] if bh >= y1 - y0 + 2 * pad and bw >= x1 - x0 + 2 * pad
                     else np.resize(bark, (y1 - y0 + 2 * pad, x1 - x0 + 2 * pad, 4)))
    albedo[y0 - pad:y1 + pad, x0 - pad:x1 + pad, :3] = patch[..., :3]
    albedo[y0 - pad:y1 + pad, x0 - pad:x1 + pad, 3] = 1.0
    normal[y0 - pad:y1 + pad, x0 - pad:x1 + pad] = (0.5, 0.5, 1.0, 1.0)
    mask[y0 - pad:y1 + pad, x0 - pad:x1 + pad] = (0.0, 1.0, 0.5, 0.1)
    # файлы пишем как есть (значения уже в кодировке файлов: альбедо sRGB, нормаль и маска линейно)
    for arr, suffix in ((albedo, '_albedo.png'), (normal, '_normal.png'), (mask, '_mask.png')):
        eg.save_png(arr, out_prefix + suffix, linear=True)


def stats_of(mb):
    co = np.array(mb.verts, np.float32)
    return dict(triangles=mb.triangle_count, size=[round(float(x), 3) for x in co.max(0) - co.min(0)],
                height=round(float(co[:, 2].max()), 3))


def bark_entry(prefix):
    return dict(id=prefix, role='bark', albedo=prefix + '_albedo.png', normal=prefix + '_normal.png', mask=prefix + '_mask.png',
                tiling=[1, 1], normalScale=1.0, smoothnessMax=1.0, aoMax=1.0, stiffness=2.0, drag=0.05, initialBend=0.3,
                heightDrag=0.0)


def leaves_entry(prefix):
    return dict(id=prefix, role='leaves', albedo=prefix + '_albedo.png', normal=prefix + '_normal.png', mask=prefix + '_mask.png',
                alphaCutoff=0.5, normalScale=0.8, aoMin=0.35, aoMax=1.0, smoothnessMax=0.3, specular=0.3,
                healthyColor=[1.0, 1.0, 1.0], dryColor=[0.86, 0.88, 0.76], colorNoiseSpread=0.3, alphaDrag=0.0,
                shiverDrag=0.08, shiverDirectionality=0.5, puddleSink=0.0, rotorWashClear=0.0, sizeVariation=0.0,
                stiffness=1.2, drag=0.08, initialBend=0.6, heightDrag=0.0, distanceControl=1.0, scaleFade=[100000.0, 30.0])


def export(out_root, name, mb, material_name, material_entry, files, nr):
    folder = os.path.join(out_root, name)
    os.makedirs(folder, exist_ok=True)
    obj = mb.to_object(name, [bpy.data.materials.new(material_name)])
    eg.smooth_normals(obj, 60.0)
    fbx = name + '.fbx'
    eg.export_fbx([obj], os.path.join(folder, fbx))
    for src in files:
        shutil.copyfile(src, os.path.join(folder, os.path.basename(src)))
    st = stats_of(mb)
    manifest = dict(name=name, type='detail', group=GROUP, height=st['height'], trunkRadius=0.0, colliderHeight=0.0,
                    evergreen=False, lods=[dict(fbx=fbx, screen=0.0, submeshes=[material_name], castShadows=True,
                                                triangles=st['triangles'])],
                    materials=[material_entry], nr=nr, size=st['size'], triangles=st['triangles'])
    with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=1)
    print('UNDERSTORY', name, json.dumps(st))


POLES = [dict(H=2.6, r=0.02, twigs=5, broken=False), dict(H=3.6, r=0.028, twigs=7, broken=True),
         dict(H=4.8, r=0.035, twigs=8, broken=False), dict(H=6.2, r=0.045, twigs=9, broken=True),
         dict(H=7.0, r=0.06, twigs=6, broken=True)]
SAPLINGS = [dict(H=1.7, r=0.012, n=5), dict(H=2.6, r=0.018, n=7), dict(H=3.6, r=0.024, n=8), dict(H=4.5, r=0.03, n=10)]
BUSHES = [dict(n=6, h=1.4), dict(n=9, h=2.2), dict(n=12, h=3.0)]
TWIGS = [dict(n=7, h=0.8), dict(n=11, h=1.3), dict(n=14, h=1.8)]
BRUSH = [dict(n=26, size=(2.4, 1.5), h=0.7), dict(n=40, size=(3.0, 1.8), h=0.9)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--tex', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--assets', nargs='*')
    args = parser.parse_args(eg.script_args())
    tex = args.tex
    os.makedirs(args.out, exist_ok=True)
    work = os.path.join(args.out, '_tex')
    os.makedirs(work, exist_ok=True)
    eg.reset_scene()
    leaves = os.path.join(work, 'understory_leaves')
    make_leaf_atlas(tex, leaves)
    dead_files = [os.path.join(tex, 'oak_dead_branch' + s) for s in ('_albedo.png', '_normal.png', '_mask.png')]
    live_files = [os.path.join(tex, 'oak_branch' + s) for s in ('_albedo.png', '_normal.png', '_mask.png')]
    leaf_files = [leaves + s for s in ('_albedo.png', '_normal.png', '_mask.png')]
    with open(os.path.join(tex, 'oak_leaves_cells.json'), encoding='utf-8') as f:
        cells = json.load(f)['cells']

    def wanted(name):
        return not args.assets or name in args.assets

    for i, spec in enumerate(POLES):
        name = f'understory_pole_{i + 1:02d}'
        if not wanted(name):
            continue
        eg.reset_scene()
        rng = random.Random(1000 + i)
        mb = eg.MeshBuilder()
        for br in build_pole(rng, spec['H'], spec['r'], spec['twigs'], spec['broken']):
            tc.sweep(mb, br, 0, 5 if br.level == 0 else 3, BRANCH_TILE, rng, cap=True)
        export(args.out, name, mb, 'oak_dead_branch', bark_entry('oak_dead_branch'), dead_files,
               dict(renderDistance=90.0, shadows=True, densityInDistance=0.7))
    for i, spec in enumerate(SAPLINGS):
        name = f'understory_sapling_{i + 1:02d}'
        if not wanted(name):
            continue
        eg.reset_scene()
        rng = random.Random(2000 + i)
        mb = eg.MeshBuilder()
        branches, tips = build_sapling(rng, spec['H'], spec['r'], spec['n'])
        for br in branches:
            sweep_rect(mb, br, 0, 5 if br.level == 0 else 3, BARK_RECT, 0.25, rng)
        leaf_cards(mb, 0, tips, cells, rng, (0.45, 0.8))
        export(args.out, name, mb, 'understory_leaves', leaves_entry('understory_leaves'), leaf_files,
               dict(renderDistance=90.0, shadows=True, densityInDistance=0.7))
    for i, spec in enumerate(BUSHES):
        name = f'understory_bush_{i + 1:02d}'
        if not wanted(name):
            continue
        eg.reset_scene()
        rng = random.Random(2500 + i)
        mb = eg.MeshBuilder()
        branches, tips = build_bush(rng, spec['n'], spec['h'])
        for br in branches:
            sweep_rect(mb, br, 0, 3, BARK_RECT, 0.25, rng, cap=False)
        leaf_cards(mb, 0, tips, cells, rng, (0.35, 0.65))
        export(args.out, name, mb, 'understory_leaves', leaves_entry('understory_leaves'), leaf_files,
               dict(renderDistance=80.0, shadows=True, densityInDistance=0.7))
    for i, spec in enumerate(TWIGS):
        name = f'understory_twigs_{i + 1:02d}'
        if not wanted(name):
            continue
        eg.reset_scene()
        rng = random.Random(3000 + i)
        mb = eg.MeshBuilder()
        for br in build_twigs(rng, spec['n'], spec['h']):
            tc.sweep(mb, br, 0, 3, BRANCH_TILE, rng, cap=False)
        export(args.out, name, mb, 'oak_dead_branch', bark_entry('oak_dead_branch'), dead_files,
               dict(renderDistance=50.0, shadows=True, densityInDistance=0.6))
    for i, spec in enumerate(BRUSH):
        name = f'understory_brush_{i + 1:02d}'
        if not wanted(name):
            continue
        eg.reset_scene()
        rng = random.Random(4000 + i)
        mb = eg.MeshBuilder()
        for br in build_brush(rng, spec['n'], spec['size'], spec['h']):
            tc.sweep(mb, br, 0, 5 if br.level == 0 else 3, BRANCH_TILE, rng, cap=True)
        export(args.out, name, mb, 'oak_branch', bark_entry('oak_branch'), live_files,
               dict(renderDistance=160.0, shadows=True, densityInDistance=1.0))


main()
