# Атласы хвои сосны обыкновенной для pine_gen.py: смоделированные концы ветвей (годичный рост побегов, щётки парной
# хвои на последних трёх годах каждой оси, почки, шишки) рендерятся ортокамерой в Cycles в атлас 2048 x 2048 (2 x 2):
#   [0] большой веер сверху (ветка 2-го порядка с 8-14 щётками, ~0.8-1 м) — основная карточка LOD0/LOD1;
#   [1] веер B сверху (другой рисунок, 1-2 шишки под щётками);
#   [2] левая половина — малый веер сверху (~0.45 м, короткие боковые веточки и концы ветвей), правая — вид сбоку на
#       большой веер (щётки загибаются вверх: толщина «подушки» на вертикальных карточках);
#   [3] «подушка» — конец скелетной ветви с несколькими веерами сверху (~1.8-2 м) для LOD1/LOD2.
# Каждый спрайт целиком внутри своей ячейки с полем (хвоя не срезается краем); точка крепления (основание оси) —
# anchor [u, v] внутри спрайта, в <prefix>_sprites.json вместе с UV-прямоугольником и размером в метрах.
# Альбедо — эмиссия цвета вершин (лицевая сторона хвоинки зелёная, изнанка сизая) с запечённой затенённостью (AO на
# масштабе хвоинки и щётки); нормаль — нормаль щётки (от оси побега наружу) + 30 % нормали хвоинки, в пространстве
# камеры = касательная нормаль карточки (OpenGL). Хвоинки толще настоящих (2.8 мм против 1.5-2), чтобы карточка
# издали читалась густой щёткой; голого дерева мало — ветка обрезана у первой щётки.
import json
import math
import os
import random
import sys

import bpy
import numpy as np
from mathutils import Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

NEEDLE_PRESETS = {
    # спелое дерево: побеги 7-11 см в год (концы ветвей в освещённой кроне), хвоя 3.5-5 см (рисуется 4.6), держится
    # 3 года -> щётка ~30 см на каждой оси, 9-10 пучков на см
    'mature': dict(seg=0.10, needle_len=0.046, needle_sd=0.006, needle_w=0.0028, fasc_per_m=980, foliated=3,
                   lat_angle=(44, 62), lat_ratio=0.72, decay=0.95, upturn=0.55, lateral_prob=1.0,
                   large=dict(years=6, seg_scale=1.15, max_order=2),
                   b=dict(years=6, seg_scale=1.05, max_order=2, cones=2),
                   small=dict(years=4, seg_scale=1.0, max_order=1, lat_angle=(28, 40)),
                   pad=dict(axis=1.5, node=0.17, years=7, seg_scale=1.05)),
    # молодое/опушечное: побеги 15-25 см в год, хвоя 5-7 см (рисуется 6.2), щётки 40-60 см — «ёршики», хвоя
    # торчит вперёд под 30-50°; боковых в мутовке 2-3, под меньшим углом — веер открытый, не плоская «лапа» ели
    'young': dict(seg=0.19, needle_len=0.062, needle_sd=0.007, needle_w=0.0030, fasc_per_m=900, foliated=3,
                  lat_angle=(34, 52), lat_ratio=0.74, decay=0.95, upturn=0.75, lateral_prob=0.9,
                  large=dict(years=4, seg_scale=1.0, max_order=1, extra_lat=1),
                  b=dict(years=4, seg_scale=0.95, max_order=2, cones=1),
                  small=dict(years=3, seg_scale=1.0, max_order=1, lat_angle=(24, 36)),
                  pad=dict(axis=1.8, node=0.3, years=4, seg_scale=1.0, extra_lat=1)),
}

# цвета хвои (sRGB): лицевая сторона тёмно-зелёная, внутренняя — сизая (устьичные полоски), скрученность показывает обе
NEEDLE_DARK = '#2e4230'
NEEDLE_MAIN = '#506a4c'
NEEDLE_GLAUC = '#75907c'
NEEDLE_OLD = '#6c7350'
NEEDLE_NEW = '#617d52'
SHOOT_GREEN = '#6e5e44'   # побег с хвоей
SHOOT_BARE = '#6c5644'    # голый побег, серо-бурый
BUD = '#8a5530'
UP = Vector((0, 0, 1))


def grow_spray(rng, P, years, seg_scale=1.0, max_order=2, lateral_prob=1.0, extra_lat=0, origin=None, direction=None,
               lat_angle=None):
    """Годичный рост ветки-веера от (0,0,0) вдоль +Y: каждый год каждая верхушка даёт прирост и мутовку — верхушечную и
    2 (+extra_lat) боковые почки почти в плоскости ветви (под lat_angle, слегка вверх). Возвращает отрезки
    [{pts, age, order, r0, r1}] (age 0 — прирост этого года) и верхушки [(pos, dir)] для почек."""
    lat_angle = lat_angle or P['lat_angle']
    tips = [dict(pos=(origin or Vector((0, 0, 0))).copy(), dir=(direction or Vector((0, 1, 0))).normalized(), order=0,
                 L=P['seg'] * seg_scale)]
    segs = []
    for y in range(years):
        age = years - 1 - y
        nxt = []
        for t in tips:
            L = t['L'] * rng.uniform(0.85, 1.15)
            d = t['dir'].copy()
            pts = [t['pos'].copy()]
            for _ in range(3):
                d = d + UP * (P['upturn'] * 0.10 * (1 + t['order'] * 0.3)) * (0.5 + y / years)
                d = d + Vector((rng.gauss(0, 0.05), rng.gauss(0, 0.05), rng.gauss(0, 0.03)))
                d.normalize()
                pts.append(pts[-1] + d * (L / 3))
            segs.append(dict(pts=pts, age=age, order=t['order']))
            nxt.append(dict(pos=pts[-1], dir=d, order=t['order'], L=t['L'] * P['decay']))
            if t['order'] < max_order and y < years - 1:
                side = d.cross(UP)
                side = side.normalized() if side.length > 1e-6 else Vector((1, 0, 0))
                n_lat = 2 + (extra_lat if t['order'] == 0 and rng.random() < 0.5 else 0)
                for j in range(n_lat):
                    s = (-1, 1)[j % 2]
                    if rng.random() > lateral_prob * (1.0 if t['order'] == 0 else 0.7):
                        continue
                    a = math.radians(rng.uniform(*lat_angle))
                    up = rng.uniform(0.05, 0.25) + (0.35 if j >= 2 else 0.0)
                    ld = d * math.cos(a) + side * (s * math.sin(a)) + UP * up
                    nxt.append(dict(pos=pts[-1], dir=ld.normalized(), order=t['order'] + 1,
                                    L=t['L'] * P['lat_ratio'] * rng.uniform(0.85, 1.1)))
        tips = nxt
    for s in segs:
        r_new = 0.0015 * (1 - 0.12 * s['order'])
        s['r1'] = r_new + 0.0005 * s['age']
        s['r0'] = r_new + 0.0005 * (s['age'] + 1)
    return segs, [(t['pos'], t['dir']) for t in tips]


class NeedleMesh:
    """Вершины/грани с атрибутами на вершинах: Col (лицевая сторона), Col2 (изнанка), brush (нормаль щётки)."""

    def __init__(self):
        self.v, self.f, self.c1, self.c2, self.nb = [], [], [], [], []

    def add(self, p, c1, c2, nb):
        self.v.append(tuple(p))
        self.c1.append(c1)
        self.c2.append(c2)
        self.nb.append(tuple(nb))
        return len(self.v) - 1

    def to_object(self, name, material):
        me = bpy.data.meshes.new(name)
        me.from_pydata(self.v, [], self.f)
        for nm, data in (('Col', self.c1), ('Col2', self.c2)):
            a = me.color_attributes.new(nm, 'FLOAT_COLOR', 'POINT')
            a.data.foreach_set('color', np.asarray(data, np.float32).ravel())
        b = me.attributes.new('brush', 'FLOAT_VECTOR', 'POINT')
        b.data.foreach_set('vector', np.asarray(self.nb, np.float32).ravel())
        me.materials.append(material)
        me.shade_smooth()
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob


def _rgba(hexs, k=1.0):
    c = eg.hex_rgb(hexs)
    return (c[0] * k, c[1] * k, c[2] * k, 1.0)


def _tube(nm, pts, r0, r1, color, sides=5):
    n = len(pts)
    prev = None
    rings = []
    for i, p in enumerate(pts):
        t = (pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]).normalized()
        nx = t.orthogonal().normalized() if prev is None else (prev - t * prev.dot(t)).normalized()
        prev = nx
        ny = t.cross(nx)
        r = r0 + (r1 - r0) * i / (n - 1)
        ring = []
        for s in range(sides):
            a = s / sides * math.tau
            o = nx * math.cos(a) + ny * math.sin(a)
            ring.append(nm.add(p + o * r, color, color, o))
        rings.append(ring)
    for i in range(n - 1):
        for s in range(sides):
            nm.f.append((rings[i][s], rings[i][(s + 1) % sides], rings[i + 1][(s + 1) % sides], rings[i + 1][s]))


def _bud(nm, rng, pos, d, scale=1.0):
    """Верхушечная почка (8-10 мм, рыжая, смолистая) и 3-4 боковые вокруг — следующая мутовка."""
    col = _rgba(BUD)
    tipc = _rgba('#a87048')
    for k in range(rng.randint(3, 4) + 1):
        if k == 0:
            bd, ln, rr = d, 0.010 * scale, 0.0024 * scale
        else:
            a = k / 4 * math.tau + rng.uniform(-0.4, 0.4)
            side = d.orthogonal().normalized()
            side.rotate(Quaternion(d, a))
            bd, ln, rr = (d * 0.8 + side * 0.6).normalized(), 0.006 * scale, 0.0017 * scale
        sides = 6
        nx = bd.orthogonal().normalized()
        ny = bd.cross(nx)
        ring = []
        for s in range(sides):
            a = s / sides * math.tau
            o = nx * math.cos(a) + ny * math.sin(a)
            ring.append(nm.add(pos + bd * (ln * 0.35) + o * rr, col, col, o))
        tip = nm.add(pos + bd * ln, tipc, tipc, bd)
        b0 = nm.add(pos, col, col, -bd)
        for s in range(sides):
            nm.f.append((ring[s], ring[(s + 1) % sides], tip))
            nm.f.append((ring[(s + 1) % sides], ring[s], b0))


def _needles_on(nm, rng, P, seg):
    """Хвоя на отрезке побега: пучки по 2 хвоинки вокруг оси, вперёд под 25-60° (молодая у верхушки прижата и
    короче), чуть вверх; хвоинка — скрученная (90-180°) слегка изогнутая полоска. Хвоя третьего года желтее."""
    pts = seg['pts']
    age = seg['age']
    lens = [(pts[i + 1] - pts[i]).length for i in range(len(pts) - 1)]
    seglen = sum(lens)
    count = max(1, int(seglen * P['fasc_per_m'] * rng.uniform(0.9, 1.1)))
    c_dark, c_main, c_gl, c_old, c_new = (np.array(eg.hex_rgb(h)) for h in
                                          (NEEDLE_DARK, NEEDLE_MAIN, NEEDLE_GLAUC, NEEDLE_OLD, NEEDLE_NEW))
    for _ in range(count):
        t = rng.random()
        acc = t * seglen
        for j, l in enumerate(lens):
            if acc <= l or j == len(lens) - 1:
                p = pts[j].lerp(pts[j + 1], min(1.0, acc / max(l, 1e-9)))
                ax = (pts[j + 1] - pts[j]).normalized()
                break
            acc -= l
        tip_zone = age == 0 and t > 0.8
        if age == 0:
            spread = rng.uniform(26, 42) * (0.65 if tip_zone else 1.0)
        elif age == 1:
            spread = rng.uniform(38, 56)
        else:
            spread = rng.uniform(44, 64)
        ln = rng.gauss(P['needle_len'], P['needle_sd']) * (0.7 if tip_zone else 1.0) * (0.93 if age == 2 else 1.0)
        ln = max(ln, P['needle_len'] * 0.45)
        radial = ax.orthogonal().normalized()
        radial.rotate(Quaternion(ax, rng.uniform(0, math.tau)))
        if radial.dot(UP) < -0.3:  # хвоя снизу горизонтального побега разворачивается в стороны
            radial = (radial + UP * 0.6).normalized()
        sa = math.radians(spread)
        mean = (ax * math.cos(sa) + radial * math.sin(sa) + UP * 0.12).normalized()
        base_col = (c_dark + (c_main - c_dark) * rng.uniform(0.25, 1.0)) * rng.uniform(0.88, 1.12)
        if age == 2:
            base_col = base_col + (c_old - base_col) * rng.uniform(0.1, 0.35)
        elif age == 0:
            base_col = base_col + (c_new - base_col) * rng.uniform(0.1, 0.3)
        back_col = base_col + (c_gl - base_col) * rng.uniform(0.3, 0.55)
        perp = mean.cross(ax)
        perp = perp.normalized() if perp.length > 1e-6 else ax.orthogonal().normalized()
        for pair in (-1, 1):
            nd = mean.copy()
            nd.rotate(Quaternion(perp, math.radians(pair * rng.uniform(4, 10))))
            nd.rotate(Quaternion(mean, math.radians(rng.uniform(-8, 8))))
            l2 = ln * rng.uniform(0.92, 1.05)
            curve_axis = nd.cross(ax)
            curve_axis = curve_axis.normalized() if curve_axis.length > 1e-6 else perp
            bend = math.radians(rng.uniform(-14, 6))
            twist = math.radians(rng.uniform(90, 180)) * rng.choice((-1, 1))
            w0 = P['needle_w'] * rng.uniform(0.9, 1.1)
            wv0 = nd.cross(radial)
            wv0 = wv0.normalized() if wv0.length > 1e-6 else perp
            q = p + radial * seg['r1']
            d = nd.copy()
            nseg = 4
            left, right = [], []
            tip = None
            for k in range(nseg + 1):
                s = k / nseg
                wv = wv0.copy()
                wv.rotate(Quaternion(d, twist * s))
                wprof = (0.65 + 0.35 * min(1.0, s / 0.3)) * (1.0 if s < 0.7 else max(0.0, (1 - s) / 0.3))
                hw = 0.5 * w0 * wprof
                shade = 0.82 + 0.18 * s
                c1 = (*(base_col * shade), 1.0)
                c2 = (*(back_col * shade), 1.0)
                if k == nseg:
                    tip = nm.add(q, c1, c2, radial)
                else:
                    left.append(nm.add(q - wv * hw, c1, c2, radial))
                    right.append(nm.add(q + wv * hw, c1, c2, radial))
                    d.rotate(Quaternion(curve_axis, bend / nseg))
                    q = q + d * (l2 / nseg)
            for k in range(nseg - 1):
                nm.f.append((left[k], right[k], right[k + 1], left[k + 1]))
            nm.f.append((left[-1], right[-1], tip))


def _cone(nm, pos, d, scale=1.0):
    """Шишка 4-5 см, серо-бурая, яйцевидная, с ромбическими щитками."""
    col_a = np.array(eg.hex_rgb('#857560'))
    col_b = np.array(eg.hex_rgb('#a89478'))
    length = 0.045 * scale
    nx = d.orthogonal().normalized()
    ny = d.cross(nx)
    rings = []
    nr, sides = 8, 10
    for i in range(nr + 1):
        s = i / nr
        rad = 0.012 * scale * math.sin(math.pi * min(1.0, s * 0.95 + 0.05)) ** 0.8 * (1.1 - 0.3 * s)
        ring = []
        for k in range(sides):
            a = k / sides * math.tau + i * 0.35
            o = nx * math.cos(a) + ny * math.sin(a)
            bump = 1.0 + 0.12 * ((i + k) % 2)
            c = col_a + (col_b - col_a) * (0.5 + 0.5 * math.sin(a * 3 + i))
            ring.append(nm.add(pos + d * (s * length) + o * rad * bump, (*c, 1.0), (*c, 1.0), o))
        rings.append(ring)
    for i in range(nr):
        for k in range(sides):
            nm.f.append((rings[i][k], rings[i][(k + 1) % sides], rings[i + 1][(k + 1) % sides], rings[i + 1][k]))


def build_spray(rng, P, name, material, years, seg_scale=1.0, max_order=2, lateral_prob=None, cones=0, extra_lat=0,
                crop_base=True, lat_angle=None):
    """Ветка-веер целиком: побеги (кора), хвоя на последних P['foliated'] годах, почки на верхушках, шишки.
    crop_base: первый (самый старый, голый) прирост оси не строится, точка крепления — первый узел (там ветвь в дереве
    переходит в карточку), так в ячейке почти одна хвоя."""
    lp = P['lateral_prob'] if lateral_prob is None else lateral_prob
    segs, tips = grow_spray(rng, P, years, seg_scale, max_order, lp, extra_lat, lat_angle=lat_angle)
    origin = Vector((0, 0, 0))
    if crop_base:
        first = [s for s in segs if s['order'] == 0 and s['age'] == years - 1][0]
        origin = first['pts'][-1].copy()
        segs = [s for s in segs if s is not first]
    nm = NeedleMesh()
    for s in segs:
        _tube(nm, s['pts'], s['r0'], s['r1'], _rgba(SHOOT_GREEN if s['age'] < P['foliated'] else SHOOT_BARE))
        if s['age'] < P['foliated']:
            _needles_on(nm, rng, P, s)
    for pos, d in tips:
        _bud(nm, rng, pos, d)
    if cones:
        old = [s for s in segs if s['age'] == P['foliated'] and s['order'] <= 1]
        rng.shuffle(old)
        for s in old[:cones]:
            _cone(nm, s['pts'][-1] + Vector((0, 0, -0.006)), Vector((rng.uniform(-0.3, 0.3), -0.4, -1.0)).normalized())
    nm.v = [(x - origin.x, y - origin.y, z - origin.z) for x, y, z in nm.v]
    return nm.to_object(name, material), segs


def build_pad(rng, P, name, material, axis=1.7, node=0.2, years=5, seg_scale=1.0, extra_lat=0):
    """«Подушка» для LOD1/LOD2 — как в дереве: внешние axis м скелетной ветви, на узлах через node м по две боковые
    веточки-веера (в плоскости ветви, под 45-60°, чуть вверх, к концу короче) и верхушечный веер; внутри голая ось."""
    nm = NeedleMesh()
    segs_all, tips_all = [], []
    n = max(2, int(axis / node))
    pts = [Vector((0, 0, 0))]
    d = Vector((0, 1, 0))
    for i in range(n):
        d = (d + UP * 0.03 + Vector((rng.gauss(0, 0.04), 0, 0))).normalized()
        pts.append(pts[-1] + d * (axis / n))
    _tube(nm, pts, 0.012, 0.006, _rgba(SHOOT_BARE), sides=6)
    flip = rng.choice((-1, 1))
    for i in range(1, n + 1):
        p = pts[i]
        ax = (pts[i] - pts[i - 1]).normalized()
        side = ax.cross(UP).normalized()
        rem = 1.0 - i / (n + 1)
        if i == n:
            dirs = [(ax + UP * 0.15).normalized()]
            yrs = [years]
        else:
            dirs = []
            yrs = []
            for sd in (flip, -flip):
                a = math.radians(rng.uniform(50, 66))
                dirs.append((ax * math.cos(a) + side * (sd * math.sin(a)) + UP * rng.uniform(0.05, 0.2)).normalized())
                yrs.append(max(2, int(round(years * (0.45 + 0.6 * rem ** 0.7)))))
            flip = -flip
        for dd, yy in zip(dirs, yrs):
            segs, tips = grow_spray(rng, P, yy, seg_scale * rng.uniform(0.9, 1.1), 1 if yy < 4 else 2,
                                    P['lateral_prob'], extra_lat, origin=p, direction=dd)
            segs_all += segs
            tips_all += tips
    for s in segs_all:
        _tube(nm, s['pts'], s['r0'], s['r1'], _rgba(SHOOT_GREEN if s['age'] < P['foliated'] else SHOOT_BARE))
        if s['age'] < P['foliated']:
            _needles_on(nm, rng, P, s)
    for pos, dd in tips_all:
        _bud(nm, rng, pos, dd)
    return nm.to_object(name, material), segs_all


def needle_render_material(name='needle_pass'):
    """Один материал на все проходы; узел PASS: 0 — альбедо с затенённостью, 1 — только затенённость, 2 — нормаль."""
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    N.clear()
    out = N.new('ShaderNodeOutputMaterial')
    em = N.new('ShaderNodeEmission')
    c1 = N.new('ShaderNodeVertexColor')
    c1.layer_name = 'Col'
    c2 = N.new('ShaderNodeVertexColor')
    c2.layer_name = 'Col2'
    geo = N.new('ShaderNodeNewGeometry')
    face = N.new('ShaderNodeMix')
    face.data_type = 'RGBA'
    L.new(geo.outputs['Backfacing'], face.inputs['Factor'])
    L.new(c1.outputs['Color'], face.inputs['A'])
    L.new(c2.outputs['Color'], face.inputs['B'])
    aos = []
    for dist, lo in ((0.02, 0.55), (0.09, 0.5)):
        ao = N.new('ShaderNodeAmbientOcclusion')
        ao.inputs['Distance'].default_value = dist
        ao.samples = 16
        ao.only_local = True
        r = N.new('ShaderNodeMapRange')
        r.inputs['To Min'].default_value = lo
        L.new(ao.outputs['AO'], r.inputs['Value'])
        aos.append(r)
    aom = N.new('ShaderNodeMath')
    aom.operation = 'MULTIPLY'
    L.new(aos[0].outputs['Result'], aom.inputs[0])
    L.new(aos[1].outputs['Result'], aom.inputs[1])
    alb = N.new('ShaderNodeMix')
    alb.data_type = 'RGBA'
    alb.blend_type = 'MULTIPLY'
    alb.inputs['Factor'].default_value = 1.0
    L.new(face.outputs['Result'], alb.inputs['A'])
    L.new(aom.outputs['Value'], alb.inputs['B'])
    br = N.new('ShaderNodeAttribute')
    br.attribute_name = 'brush'
    brn = N.new('ShaderNodeVectorMath')
    brn.operation = 'NORMALIZE'
    L.new(br.outputs['Vector'], brn.inputs[0])
    nmix = N.new('ShaderNodeMix')
    nmix.data_type = 'VECTOR'
    nmix.inputs['Factor'].default_value = 0.3
    L.new(brn.outputs['Vector'], nmix.inputs['A'])
    L.new(geo.outputs['Normal'], nmix.inputs['B'])
    nn = N.new('ShaderNodeVectorMath')
    nn.operation = 'NORMALIZE'
    L.new(nmix.outputs['Result'], nn.inputs[0])
    vt = N.new('ShaderNodeVectorTransform')
    vt.vector_type = 'NORMAL'
    vt.convert_from = 'WORLD'
    vt.convert_to = 'CAMERA'
    L.new(nn.outputs['Vector'], vt.inputs['Vector'])
    enc = N.new('ShaderNodeVectorMath')
    enc.operation = 'MULTIPLY_ADD'
    enc.inputs[1].default_value = (0.5, 0.5, -0.5)
    enc.inputs[2].default_value = (0.5, 0.5, 0.5)
    L.new(vt.outputs['Vector'], enc.inputs[0])
    pas = N.new('ShaderNodeValue')
    pas.name = 'PASS'
    gt0 = N.new('ShaderNodeMath')
    gt0.operation = 'GREATER_THAN'
    gt0.inputs[1].default_value = 0.5
    L.new(pas.outputs[0], gt0.inputs[0])
    gt1 = N.new('ShaderNodeMath')
    gt1.operation = 'GREATER_THAN'
    gt1.inputs[1].default_value = 1.5
    L.new(pas.outputs[0], gt1.inputs[0])
    aoc = N.new('ShaderNodeCombineColor')
    for i in range(3):
        L.new(aom.outputs['Value'], aoc.inputs[i])
    m1 = N.new('ShaderNodeMix')
    m1.data_type = 'RGBA'
    L.new(gt0.outputs[0], m1.inputs['Factor'])
    L.new(alb.outputs['Result'], m1.inputs['A'])
    L.new(aoc.outputs['Color'], m1.inputs['B'])
    m2 = N.new('ShaderNodeMix')
    m2.data_type = 'RGBA'
    L.new(gt1.outputs[0], m2.inputs['Factor'])
    L.new(m1.outputs['Result'], m2.inputs['A'])
    L.new(enc.outputs['Vector'], m2.inputs['B'])
    L.new(m2.outputs['Result'], em.inputs['Color'])
    L.new(em.outputs['Emission'], out.inputs['Surface'])
    return m


def setup_gpu(scene, samples):
    eg.setup_card_render(scene, 64, 64, samples=samples)
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        prefs.compute_device_type = 'CUDA'
        prefs.get_devices()
        for d in prefs.devices:
            d.use = d.type == 'CUDA'
        scene.cycles.device = 'GPU'
    except Exception:
        scene.cycles.device = 'CPU'


def render_sprite(scene, mat, view, lat0, lat1, lon0, lon1, px_w, px_h, tmp, tag):
    """Ортокамера: view 'top' (сверху: вправо +X, вверх +Y) или 'side' (с -X: вправо +Z, вверх +Y). Кадр по поперечной
    оси от lat0 до lat1, по продольной (Y) от lon0 до lon1. Возвращает (альбедо RGBA, ao, нормаль RGB) линейно."""
    scale = max(lat1 - lat0, lon1 - lon0)
    lc = 0.5 * (lat0 + lat1)
    yc = 0.5 * (lon0 + lon1)
    if view == 'top':
        loc, rot = (lc, yc, 5.0), (0, 0, 0)
    else:
        loc, rot = (-5.0, yc, lc), (0, math.radians(-90), 0)
    cam = eg.ortho_camera(scene, loc, rot, scale, name='spr_cam')
    scene.render.resolution_x, scene.render.resolution_y = px_w, px_h
    res = []
    for k in range(3):
        mat.node_tree.nodes['PASS'].outputs[0].default_value = float(k)
        path = os.path.join(tmp, f'{tag}_{k}.png')
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        res.append(eg.srgb_png_to_linear_values(path))
    bpy.data.objects.remove(cam)
    return res[0], res[1][..., 0], res[2][..., :3]


def _frame(obj, view, pw, ph, margin=0.03):
    """Кадр спрайта: весь объект с полем margin, пропорции ячейки pw:ph; поперечная ось симметрична относительно
    содержимого. Возвращает (lat0, lat1, lon0, lon1)."""
    vs = np.array([v.co[:] for v in obj.data.vertices])
    lateral = vs[:, 0] if view == 'top' else vs[:, 2]
    lon = vs[:, 1]
    l0, l1 = float(lateral.min()), float(lateral.max())
    b0, b1 = float(lon.min()), float(lon.max())
    w, h = (l1 - l0) * (1 + 2 * margin), (b1 - b0) * (1 + 2 * margin)
    aspect = pw / ph
    if w / h > aspect:
        h = w / aspect
    else:
        w = h * aspect
    lc, bc = 0.5 * (l0 + l1), 0.5 * (b0 + b1)
    return lc - w / 2, lc + w / 2, bc - h / 2, bc + h / 2


def needle_atlas(out_dir, preset='mature', seed=7, cell_px=1024, samples=48, prefix=None):
    """Атлас 2 x 2 ячеек (см. начало файла). Пишет PNG (альбедо RGBA, нормаль, маска) и <prefix>_sprites.json:
    UV-прямоугольник [u0, v0, u1, v1], размер в метрах [ширина, длина], точка крепления anchor [u, v] (доли спрайта),
    вид, доля непрозрачного."""
    P = NEEDLE_PRESETS[preset]
    prefix = prefix or f'pine_needles_{preset}'
    rng = random.Random(seed)
    scene = eg.reset_scene()
    setup_gpu(scene, samples)
    mat = needle_render_material()
    tmp = os.path.join(out_dir, '_needle_tmp')
    os.makedirs(tmp, exist_ok=True)
    S = cell_px
    atlas = np.zeros((2 * S, 2 * S, 4), np.float32)
    ao_atlas = np.ones((2 * S, 2 * S), np.float32)
    nrm_atlas = np.zeros((2 * S, 2 * S, 3), np.float32)
    sprites = {}
    objs = []

    def place(name, obj, view, x0, y0, pw, ph):
        for o in objs:
            o.hide_render = o is not obj
        l0, l1, b0, b1 = _frame(obj, view, pw, ph)
        alb, ao, nrm = render_sprite(scene, mat, view, l0, l1, b0, b1, pw, ph, tmp, name)
        atlas[y0:y0 + ph, x0:x0 + pw] = alb
        ao_atlas[y0:y0 + ph, x0:x0 + pw] = np.where(alb[..., 3] > 0.01, ao, 1.0)
        nrm_atlas[y0:y0 + ph, x0:x0 + pw] = nrm
        a = alb[..., 3]
        edge = max(float(a[0].max()), float(a[-1].max()), float(a[:, 0].max()), float(a[:, -1].max()))
        sprites[name] = dict(uv=[x0 / (2 * S), 1 - (y0 + ph) / (2 * S), (x0 + pw) / (2 * S), 1 - y0 / (2 * S)],
                             size=[l1 - l0, b1 - b0], anchor=[(0.0 - l0) / (l1 - l0), (0.0 - b0) / (b1 - b0)],
                             view=view, coverage=float((a > 0.5).mean()), edge_alpha=edge)

    a, _ = build_spray(rng, P, 'large', mat, **P['large'])
    b, _ = build_spray(rng, P, 'b', mat, **P['b'])
    sm, _ = build_spray(rng, P, 'small', mat, **P['small'])
    pad, _ = build_pad(rng, P, 'pad', mat, **P['pad'])
    objs.extend([a, b, sm, pad])
    place('spray_large', a, 'top', 0, 0, S, S)
    place('spray_b', b, 'top', S, 0, S, S)
    place('spray_small', sm, 'top', 0, S, S // 2, S)
    place('spray_side', a, 'side', S // 2, S, S // 2, S)
    place('pad', pad, 'top', S, S, S, S)

    filled = atlas[..., 3] > 0.01
    al = eg.dilate_rgb(atlas, 16)
    nrm = np.where(filled[..., None], nrm_atlas, np.array([0.5, 0.5, 1.0], np.float32))
    nrm = eg.dilate_rgb(np.concatenate([nrm, atlas[..., 3:4]], -1), 6)[..., :3]
    # гладкость: хвоя восковая 0.3, побеги 0.15 (по цвету: бурые пиксели — побеги)
    brown = np.clip((atlas[..., 0] - atlas[..., 1]) * 8.0, 0, 1)
    smooth = 0.30 - 0.15 * brown
    mask = np.stack([np.zeros_like(ao_atlas), ao_atlas, np.full_like(ao_atlas, 0.5), smooth], -1)
    names = [prefix + '_albedo.png', prefix + '_normal.png', prefix + '_mask.png']
    eg.save_png(al, os.path.join(out_dir, names[0]))
    eg.save_png(nrm, os.path.join(out_dir, names[1]), linear=True)
    eg.save_png(mask, os.path.join(out_dir, names[2]), linear=True)
    with open(os.path.join(out_dir, prefix + '_sprites.json'), 'w') as f:
        json.dump(sprites, f, indent=1)
    print('ATLAS', prefix, json.dumps(sprites))
    return names, sprites


if __name__ == '__main__':
    args = eg.script_args()
    out = args[0]
    which = args[1:] or ['mature', 'young']
    os.makedirs(out, exist_ok=True)
    for w in which:
        needle_atlas(out, w, seed=7 if w == 'mature' else 11)
