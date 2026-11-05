# Обломки разрушенных зданий для полей боя GeoWorld (Tools~/TreeGen). Бой закончен: ни огня, ни дыма — копоть и
# обгар только старыми пятнами. Двенадцать ассетов в трёх классах размера; у класса — один атлас 2048² (альбедо sRGB,
# нормаль OpenGL и маска NatureManufacture линейно: R металличность 0, G AO, B высота 0.5, A гладкость 0.08–0.3) и один
# материал роли bark (NM_Bark NR; ветер выключен: цвет вершин (1, 1, 1, 0), _Stiffness 1, _Drag 0.0001). Деталь Nature
# Renderer — ровно один материал, поэтому все куски класса лежат в одном атласе.
#  - мелкие (debris_small: детали без теней, 35 м): debris_bits_brick_a/_b — кирпичи целые, половинки и осколки, красные
#    и силикатные вперемешку; debris_bits_concrete — бетонная крошка с двумя обрезками арматуры; debris_shards_slate —
#    осколки волнистого шифера 10–45 см (волна 177 мм); debris_bits_wood — щепа и обломки реек;
#  - средние (debris_medium: детали с тенями, 100 м): debris_brick_chunk — кусок кладки (перевязка, ступени по швам,
#    выпавший кирпич, остатки штукатурки); debris_concrete_chunk — обломок бетона с арматурой; debris_slate_sheet —
#    разбитый лист шифера 1.1 × 0.8 м; debris_planks — доска с расщепом, брусок и доска внахлёст, ржавые гвозди;
#  - крупные (debris_large: детали с тенями, 200 м, и двойники с коллайдером <name>_prop): debris_slab — плита
#    перекрытия, треснувшая надвое, половинки держит арматура; debris_iron_sheet — смятый лист кровельного железа (обе
#    стороны — своей геометрией: NM_Bark рисует только лицевую; волна — в карте нормалей); debris_beam — сломанная
#    стропильная балка с расщепом и гвоздями.
# Материалы — процедурные шейдеры библиотеки руин (ruinmats: кладка, бетон, излом бетона, арматура, штукатурка; подтёки,
# копоть, пыль), чего там нет — свои в том же духе на той же основе (NB, Ctx, _finish_color): отдельный кирпич и его
# излом, шифер, кровельное железо, выветренная древесина и её расщеп. Каждый кусок строится в своей «материальной»
# системе (рисунок кладки по курсам, волокна вдоль X, волна шифера по атрибуту sheet), лежит в позе ассета; координаты
# шума у кусков разнесены, чтобы одинаковые куски не повторяли рисунок. Кучка складывается по одному: кусок ложится по
# плоскости опоры под ним и опускается лучами до первого касания земли или уже лежащих (BVH); лежащее на земле утоплено
# на несколько миллиметров (крупное — на 2–3 см), грани, ушедшие в землю, срезаны. Пивот — земля (z = 0) под серединой.
# Запечка — Cycles только на процессоре (видеокарта занята редактором Unity): номер материала (EMIT), цвет (DIFFUSE
# COLOR), нормаль (касательная, OpenGL: рельеф шейдера + скругление рёбер узлом Bevel), шероховатость, AO с землёй.
# Цвет каждого материала сведён к палитре руин (envgen_common.grade_albedo, насыщенность 0.8, средние — target у
# псевдонима; кирпич красный ~(0.17, 0.105, 0.07), силикатный ~(0.12, 0.118, 0.105), шифер — серый #6D6D6D–#929292
# игры), сверху — лёгкая пыль на гранях, смотрящих вверх, светлее рёбра (свежий скол). Атлас класса копируется в папку
# каждого ассета (как в deadwood_cut_gen): VegetationImporter кладёт их одним файлом в Generated/Debris.
# Запуск (Blender 5.2, фоном):
#   blender.exe -b --factory-startup --python debris_gen.py -- [<папка выдачи>] [--draft] [--assets <имя> ...]
#       [--ruinmats <папка с ruinmats.py>] [--no-preview] [--threads N]
#   --draft — атлас 512², мало сэмплов (проверка); без папки — Tools~/TreeGen/out/Debris.
import argparse
import json
import math
import os
import random
import shutil
import sys
import time
import zlib

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import envgen_common as eg  # noqa: E402

RUINMATS = r'F:/Unity Projects/Building Extractor/buildings-extractor-/ruins/tools'
OUT_DEFAULT = os.path.join(HERE, 'out', 'Debris')
GROUP = 'Debris'
NO_WIND = (1.0, 1.0, 1.0, 0.0)
SATURATION = 0.8
UP = Vector((0.0, 0.0, 1.0))
PITCH = 0.177            # волна шифера
AMP = 0.022              # полувысота волны шифера (44 мм)
SLATE_T = 0.006          # толщина листа шифера
IRON_PITCH = 0.125       # волна кровельного железа (в карте нормалей)
DUST = (0.40, 0.37, 0.33)
rm = None                # ruinmats (main)

CLASSES = {
    'small': dict(material='debris_small', size=2048, target_ppm=500, margin=0.004, bevel=0.004, bump=0.003, dust=0.32,
                  ao_dist=0.12, spacing=3.0, cast=False, prop=False,
                  nr=dict(renderDistance=35.0, shadows=False, densityInDistance=0.5)),
    'medium': dict(material='debris_medium', size=2048, target_ppm=250, margin=0.004, bevel=0.010, bump=0.006, dust=0.30,
                   ao_dist=0.35, spacing=4.5, cast=True, prop=False,
                   nr=dict(renderDistance=100.0, shadows=True, densityInDistance=0.8)),
    'large': dict(material='debris_large', size=2048, target_ppm=170, margin=0.004, bevel=0.015, bump=0.010, dust=0.28,
                  ao_dist=0.6, spacing=6.0, cast=True, prop=True,
                  nr=dict(renderDistance=200.0, shadows=True, densityInDistance=1.0)),
}

# Псевдонимы материалов: вид шейдера (свой или ruinmats.KINDS), параметры, средний цвет после цветокоррекции (линейно);
# bevel/bump/dust/edge — переопределения класса.
ALIASES = {
    'brick_red': dict(kind='brick_unit', target=(0.17, 0.105, 0.07),
                      params=dict(brick_a=(0.43, 0.19, 0.13), brick_b=(0.33, 0.15, 0.10), mortar=(0.50, 0.48, 0.44))),
    'brick_red_broken': dict(kind='brick_frac', target=(0.19, 0.11, 0.072),
                             params=dict(base=(0.47, 0.21, 0.14), grain=(0.62, 0.42, 0.30))),
    'brick_sil': dict(kind='brick_unit', target=(0.12, 0.118, 0.105),
                      params=dict(brick_a=(0.72, 0.70, 0.64), brick_b=(0.64, 0.62, 0.57), mortar=(0.55, 0.53, 0.48))),
    'brick_sil_broken': dict(kind='brick_frac', target=(0.135, 0.132, 0.118),
                             params=dict(base=(0.66, 0.64, 0.59), grain=(0.80, 0.78, 0.72))),
    'brickwork': dict(kind='brick', target=(0.16, 0.10, 0.072), params=dict()),
    # усиление цвета — как у кладки без штукатурки (иначе кирпич на лице темнее, чем на сколах)
    'brickwork_plaster': dict(kind='brick', target=(0.2, 0.165, 0.13), grade_like='brickwork',
                              params=dict(plaster=(0.74, 0.70, 0.60), plaster_cover=0.3)),
    'concrete': dict(kind='concrete', target=(0.14, 0.135, 0.12), params=dict(base=(0.46, 0.45, 0.42))),
    'ceiling': dict(kind='plaster', target=(0.17, 0.165, 0.15),
                    params=dict(plaster=(0.72, 0.71, 0.66), substrate=(0.45, 0.44, 0.42), substrate_kind='adobe',
                                plaster_cover=0.55)),
    'broken': dict(kind='broken', target=(0.165, 0.16, 0.145), params=dict(rust=0.35), edge=0.25),
    'rebar': dict(kind='rebar', target=(0.10, 0.052, 0.03), params=dict(), bevel=0.0, dust=0.1, edge=0.0, vary=0.12),
    'slate': dict(kind='slate', target=(0.155, 0.155, 0.148), params=dict(base=(0.40, 0.40, 0.38)), bevel=0.0015,
                  edge=0.2),
    'slate_edge': dict(kind='slate_edge', target=(0.20, 0.20, 0.19), params=dict(), bevel=0.0015, edge=0.0),
    'iron_top': dict(kind='iron', target=(0.085, 0.095, 0.072), params=dict(side=1.0, paint=(0.24, 0.34, 0.24)),
                     bevel=0.0, bump=0.018, bump_strength=1.0, dust=0.22, edge=0.0),
    'iron_bottom': dict(kind='iron', target=(0.105, 0.10, 0.09), params=dict(side=-1.0), bevel=0.0, bump=0.018,
                        bump_strength=1.0, dust=0.15, edge=0.0),
    'wood': dict(kind='wood', target=(0.11, 0.095, 0.078), params=dict()),
    'wood_split': dict(kind='wood_split', target=(0.15, 0.12, 0.088), params=dict()),
}
ALIAS_IDS = {a: i + 1 for i, a in enumerate(ALIASES)}


# ================================================================== геометрия

def newell(pts):
    n = Vector((0.0, 0.0, 0.0))
    for i in range(len(pts)):
        a, b = pts[i], pts[(i + 1) % len(pts)]
        n.x += (a.y - b.y) * (a.z + b.z)
        n.y += (a.z - b.z) * (a.x + b.x)
        n.z += (a.x - b.x) * (a.y + b.y)
    return n


class DMesh:
    """Меш куска в его материальной системе: вершины, грани (индексы, псевдоним, UV углов или None), атрибуты вершин."""

    def __init__(self):
        self.verts = []
        self.faces = []
        self.vattr = {}

    def v(self, co):
        self.verts.append(Vector(co))
        return len(self.verts) - 1

    def f(self, idx, alias, uv=None, expect=None):
        idx = list(idx)
        if len(set(idx)) < 3:
            return
        uv = list(uv) if uv is not None else None
        if expect is not None and newell([self.verts[i] for i in idx]).dot(Vector(expect)) < 0:
            idx.reverse()
            if uv is not None:
                uv.reverse()
        self.faces.append([tuple(idx), alias, uv])

    def tris(self):
        return sum(len(f[0]) - 2 for f in self.faces)

    def append(self, other, matrix=None):
        off = len(self.verts)
        for v in other.verts:
            self.verts.append(matrix @ v if matrix is not None else v.copy())
        for idx, alias, uv in other.faces:
            self.faces.append([tuple(i + off for i in idx), alias, uv])
        for k, d in other.vattr.items():
            self.vattr.setdefault(k, {}).update({i + off: val for i, val in d.items()})

    def compact(self):
        used = sorted({i for f in self.faces for i in f[0]})
        remap = {o: n for n, o in enumerate(used)}
        self.verts = [self.verts[o] for o in used]
        self.faces = [[tuple(remap[i] for i in f[0]), f[1], f[2]] for f in self.faces]
        self.vattr = {k: {remap[i]: v for i, v in d.items() if i in remap} for k, d in self.vattr.items()}

    def translate(self, d):
        d = Vector(d)
        self.verts = [v + d for v in self.verts]

    def bounds(self):
        xs = [v.x for v in self.verts]
        ys = [v.y for v in self.verts]
        zs = [v.z for v in self.verts]
        return Vector((min(xs), min(ys), min(zs))), Vector((max(xs), max(ys), max(zs)))


class Piece:
    def __init__(self, mesh, pose=None, smooth=None, auto_uv=True, soot=0.0, offset=None):
        self.mesh = mesh
        self.pose = pose if pose is not None else Matrix.Identity(4)
        self.smooth = smooth        # None — плоские грани; угол (°) — гладко, острые рёбра круче угла
        self.auto_uv = auto_uv
        self.soot = soot
        self.offset = offset        # сдвиг координат шума (None — случайный)


class Asset:
    def __init__(self, name, cls, budget):
        self.name, self.cls, self.budget = name, cls, budget
        self.pieces = []
        self.info = {}

    def tris(self):
        return sum(p.mesh.tris() for p in self.pieces)


def rand_unit(rng):
    z = rng.uniform(-1.0, 1.0)
    a = rng.uniform(0.0, 2 * math.pi)
    s = math.sqrt(max(0.0, 1 - z * z))
    return Vector((s * math.cos(a), s * math.sin(a), z))


def rot_to(n):
    """Поворот, переводящий +Z в n."""
    n = Vector(n).normalized()
    axis = UP.cross(n)
    if axis.length < 1e-8:
        return Matrix.Identity(4) if n.z > 0 else Matrix.Rotation(math.pi, 4, 'X')
    return Matrix.Rotation(math.acos(max(-1.0, min(1.0, UP.dot(n)))), 4, axis.normalized())


def tilt_jitter(n, deg, rng):
    if deg <= 0:
        return n
    axis = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0.0))
    if axis.length < 1e-6:
        return n
    return (Matrix.Rotation(math.radians(rng.uniform(0.0, deg)), 3, axis.normalized()) @ n).normalized()


def hull_mesh(points, alias_fn):
    """Выпуклая оболочка точек -> DMesh; alias_fn(нормаль, центр грани) -> псевдоним."""
    bm = bmesh.new()
    for p in points:
        bm.verts.new(p)
    res = bmesh.ops.convex_hull(bm, input=bm.verts[:], use_existing_faces=False)
    junk = [g for g in res['geom_interior'] + res['geom_unused'] if isinstance(g, bmesh.types.BMVert)]
    if junk:
        bmesh.ops.delete(bm, geom=list(set(junk)), context='VERTS')
    loose = [v for v in bm.verts if not v.link_faces]
    if loose:
        bmesh.ops.delete(bm, geom=loose, context='VERTS')
    bm.normal_update()
    centre = sum((v.co for v in bm.verts), Vector()) / max(1, len(bm.verts))
    m = DMesh()
    idx = {v: m.v(v.co) for v in bm.verts}
    for f in bm.faces:
        vs = list(f.verts)
        c = f.calc_center_median()
        n = f.normal.copy()
        if n.dot(c - centre) < 0:
            vs.reverse()
            n = -n
        m.f([idx[v] for v in vs], alias_fn(n, c))
    bm.free()
    return m


def merge_coplanar(m):
    """Слить соседние грани одной плоскости и одного материала (кладка из ячеек), вернуть новый DMesh."""
    aliases = sorted(set(f[1] for f in m.faces))
    bm = bmesh.new()
    bv = [bm.verts.new(v) for v in m.verts]
    for idx, alias, uv in m.faces:
        try:
            f = bm.faces.new([bv[i] for i in idx])
        except ValueError:
            continue
        f.material_index = aliases.index(alias)
    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-6)
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(1.0), use_dissolve_boundaries=False, verts=bm.verts[:],
                             edges=bm.edges[:], delimit={'MATERIAL'})
    loose = [v for v in bm.verts if not v.link_faces]
    if loose:
        bmesh.ops.delete(bm, geom=loose, context='VERTS')
    out = DMesh()
    idx = {v: out.v(v.co) for v in bm.verts}
    for f in bm.faces:
        out.f([idx[v] for v in f.verts], aliases[f.material_index])
    bm.free()
    return out


def tube(m, pts, r, sides, alias, cap0=True, cap1=True, roll=0.0):
    """Пруток по ломаной pts (арматура, гвоздь): кольца с параллельным переносом рамки."""
    pts = [Vector(p) for p in pts]
    n = len(pts)
    rings = []
    prev = None
    for i, p in enumerate(pts):
        if i == 0:
            t = (pts[1] - pts[0]).normalized()
        elif i == n - 1:
            t = (pts[-1] - pts[-2]).normalized()
        else:
            t = ((pts[i + 1] - pts[i]).normalized() + (pts[i] - pts[i - 1]).normalized()).normalized()
        if prev is None:
            a = t.orthogonal().normalized()
        else:
            a = prev - t * prev.dot(t)
            a = a.normalized() if a.length > 1e-6 else t.orthogonal().normalized()
        prev = a
        b = t.cross(a).normalized()
        ring = [m.v(p + (a * math.cos(2 * math.pi * k / sides + roll) + b * math.sin(2 * math.pi * k / sides + roll)) * r)
                for k in range(sides)]
        rings.append((ring, p))
    for i in range(n - 1):
        (ra, pa), (rb, pb) = rings[i], rings[i + 1]
        for k in range(sides):
            k2 = (k + 1) % sides
            mid = (m.verts[ra[k]] + m.verts[ra[k2]] + m.verts[rb[k]] + m.verts[rb[k2]]) / 4
            m.f([ra[k], ra[k2], rb[k2], rb[k]], alias, expect=mid - (pa + pb) / 2)
    if cap0:
        m.f(rings[0][0], alias, expect=pts[0] - pts[1])
    if cap1:
        m.f(rings[-1][0], alias, expect=pts[-1] - pts[-2])


# ------------------------------------------------------------------ укладка: кусок ложится на землю и на уже лежащие

class Pile:
    def __init__(self, ground=0.0):
        self.ground = ground
        self.verts = []
        self.polys = []
        self.bvh = None

    def add(self, W, polys):
        off = len(self.verts)
        self.verts.extend(W)
        self.polys.extend(tuple(i + off for i in p) for p in polys)
        self.bvh = BVHTree.FromPolygons(self.verts, self.polys)

    def support(self, x, y):
        z = self.ground
        if self.bvh is not None:
            loc = self.bvh.ray_cast(Vector((x, y, 50.0)), Vector((0.0, 0.0, -1.0)))[0]
            if loc is not None:
                z = max(z, loc.z)
        return z

    def drop(self, W, polys):
        best = min(w.z for w in W) - self.ground
        on_ground = True
        if self.bvh is not None:
            down = Vector((0.0, 0.0, -1.0))
            for w in W:
                loc, nrm, i, dist = self.bvh.ray_cast(w, down)
                if loc is not None and dist < best:
                    best, on_ground = dist, False
            mine = BVHTree.FromPolygons(W, polys)
            x0, x1 = min(w.x for w in W), max(w.x for w in W)
            y0, y1 = min(w.y for w in W), max(w.y for w in W)
            up = Vector((0.0, 0.0, 1.0))
            for p in self.verts:
                if x0 <= p.x <= x1 and y0 <= p.y <= y1:
                    loc, nrm, i, dist = mine.ray_cast(p, up)
                    if loc is not None and dist < best:
                        best, on_ground = dist, False
        return best, on_ground

    def place(self, mesh, x, y, yaw, rng, sink=0.005, jitter=2.5, max_tilt=22.0, base=None):
        """Поза куска: наклон по плоскости опоры под ним (5 лучей), затем вниз до первого касания."""
        base = base if base is not None else Matrix.Identity(4)
        rest = [base @ v for v in mesh.verts]
        lo = Vector((min(v.x for v in rest), min(v.y for v in rest), 0.0))
        hi = Vector((max(v.x for v in rest), max(v.y for v in rest), 0.0))
        c = (lo + hi) / 2
        h = (hi - lo) / 2 * 0.7
        ryaw = Matrix.Rotation(yaw, 4, 'Z')
        pts = []
        for sx, sy in ((0, 0), (-1, -1), (1, -1), (-1, 1), (1, 1)):
            w = ryaw @ Vector((c.x + sx * h.x, c.y + sy * h.y, 0.0))
            px, py = x + w.x, y + w.y
            pts.append((px, py, self.support(px, py)))
        a = np.array([[p[0], p[1], 1.0] for p in pts])
        b = np.array([p[2] for p in pts])
        sol = np.linalg.lstsq(a, b, rcond=None)[0]
        n = Vector((-sol[0], -sol[1], 1.0)).normalized()
        if math.degrees(math.acos(min(1.0, n.z))) > max_tilt:
            hor = Vector((n.x, n.y, 0.0)).normalized()
            t = math.radians(max_tilt)
            n = (hor * math.sin(t) + UP * math.cos(t)).normalized()
        n = tilt_jitter(n, jitter, rng)
        M = Matrix.Translation((x, y, 0.0)) @ rot_to(n) @ ryaw @ base
        W = [M @ v for v in mesh.verts]
        dz = max(p[2] for p in pts) + 0.5 - min(w.z for w in W)
        W = [w + Vector((0.0, 0.0, dz)) for w in W]
        drop, on_ground = self.drop(W, [f[0] for f in mesh.faces])
        final = dz - drop - (sink if on_ground else 0.0015)
        return Matrix.Translation((0.0, 0.0, final)) @ M, on_ground


class Disc:
    """Пятно кучки: точки — вокруг 2–3 центров и по всему кругу; куски не дальше limit от середины."""

    def __init__(self, radius, rng, clusters=2, sigma=0.11, share=0.65, limit=None):
        self.r = radius
        self.limit = limit if limit is not None else radius + 0.1
        self.sigma, self.share = sigma, share
        self.centres = [(rng.uniform(-0.45, 0.45) * radius, rng.uniform(-0.45, 0.45) * radius) for _ in range(clusters)]

    def sample(self, rng):
        if rng.random() < self.share:
            cx, cy = rng.choice(self.centres)
            return cx + rng.gauss(0.0, self.sigma), cy + rng.gauss(0.0, self.sigma)
        rr = self.r * math.sqrt(rng.random())
        a = rng.uniform(0.0, 2 * math.pi)
        return rr * math.cos(a), rr * math.sin(a)

    def inside(self, W):
        return all(w.x * w.x + w.y * w.y <= self.limit * self.limit for w in W)


def strip_bottom(mesh, M, zmax=0.012, nz=-0.75):
    """Грани снизу, ушедшие в землю (или лежащие на ней), не видны — их нет."""
    keep = []
    for f in mesh.faces:
        pts = [M @ mesh.verts[i] for i in f[0]]
        n = newell(pts)
        if n.length > 1e-12 and n.normalized().z < nz and max(p.z for p in pts) < zmax:
            continue
        keep.append(f)
    mesh.faces = keep


def drop_piece(asset, pile, mesh, rng, area=None, sink=0.005, jitter=2.5, base=None, max_height=None, yaw=None, xy=None,
               tries=40, smooth=None, soot=0.0, auto_uv=True, keep_bottom=False, offset=None):
    for _ in range(tries):
        px, py = xy if xy is not None else area.sample(rng)
        yw = rng.uniform(0.0, 2 * math.pi) if yaw is None else yaw
        M, on_ground = pile.place(mesh, px, py, yw, rng, sink=sink, jitter=jitter, base=base)
        W = [M @ v for v in mesh.verts]
        if (area is None or area.inside(W)) and (max_height is None or max(w.z for w in W) <= max_height):
            break
    else:
        return None
    pile.add(W, [f[0] for f in mesh.faces])
    if on_ground and not keep_bottom:
        strip_bottom(mesh, M)
    piece = Piece(mesh, M, smooth=smooth, auto_uv=auto_uv, soot=soot, offset=offset)
    asset.pieces.append(piece)
    return piece


def recentre(asset):
    pts = [p.pose @ v for p in asset.pieces for v in p.mesh.verts]
    cx = (min(v.x for v in pts) + max(v.x for v in pts)) / 2
    cy = (min(v.y for v in pts) + max(v.y for v in pts)) / 2
    t = Matrix.Translation((-cx, -cy, 0.0))
    for p in asset.pieces:
        p.pose = t @ p.pose


# ------------------------------------------------------------------ кирпич, крошка

def brick_aliases(kind):
    s = 'brick_red' if kind == 'red' else 'brick_sil'
    return s, s + '_broken'


def box_alias(lo, hi, surf, brk, tol=0.0025):
    """Грань на плоскости исходного бруска — его поверхность, остальное — излом."""
    def fn(n, c):
        for ax in range(3):
            if abs(n[ax]) > 0.985:
                plane = hi[ax] if n[ax] > 0 else lo[ax]
                if abs(c[ax] - plane) < tol:
                    return surf
        return brk
    return fn


def mk_brick(rng, kind, length, broken=(), chips=0, edge=False, W=0.12, H=0.065):
    """Кирпич 250 × 120 × 65 (или его часть длиной length) вдоль X, низ на z = 0. broken — концы '-'/'+' изломом,
    chips — сколотые углы, edge — лежит на ребре (ложком вниз)."""
    surf, brk = brick_aliases(kind)
    if edge:
        W, H = H, W
    hx = length / 2
    pts = []
    corners = [(sx, sy, z) for sx in (-1, 1) for sy in (-1, 1) for z in (0.0, H)]
    chip_ids = set(rng.sample(range(8), min(8, chips))) if chips else set()
    for k, (sx, sy, z) in enumerate(corners):
        end_broken = ('+' if sx > 0 else '-') in broken
        x = sx * hx + (sx * rng.uniform(-0.024, 0.006) if end_broken else 0.0)
        p = Vector((x, sy * W / 2, z))
        if k in chip_ids and not end_broken:
            d = rng.uniform(0.008, 0.024)
            for dv in (Vector((-sx, 0, 0)), Vector((0, -sy, 0)), Vector((0, 0, -1.0 if z > 0 else 1.0))):
                pts.append(p + dv * d * rng.uniform(0.6, 1.4))
        else:
            pts.append(p)
    for s in broken:
        sx = 1 if s == '+' else -1
        for _ in range(rng.randint(1, 3)):
            pts.append(Vector((sx * (hx + rng.uniform(-0.004, 0.016)), rng.uniform(-0.8, 0.8) * W / 2,
                               rng.uniform(0.15, 0.85) * H)))
    return hull_mesh(pts, box_alias((-hx, -W / 2, 0.0), (hx, W / 2, H), surf, brk))


def flat_bottomed(pts, frac):
    z0 = min(p.z for p in pts)
    z1 = max(p.z for p in pts)
    cut = z0 + (z1 - z0) * frac
    return [Vector((p.x, p.y, max(p.z, cut) - cut)) for p in pts], z1 - cut


def mk_chip(rng, kind, a, flat_top=0.5):
    """Осколок кирпича ~a (полудлина): оболочка 7–10 точек, низ плоский, иногда верх — исходная грань."""
    surf, brk = brick_aliases(kind)
    pts = []
    for _ in range(rng.randint(7, 10)):
        d = rand_unit(rng)
        r = rng.uniform(0.55, 1.0)
        pts.append(Vector((d.x * a * r, d.y * a * 0.72 * r, d.z * a * 0.5 * r)))
    pts, h = flat_bottomed(pts, 0.2)
    top = None
    if rng.random() < flat_top:
        top = h * rng.uniform(0.7, 0.85)
        pts = [Vector((p.x, p.y, min(p.z, top))) for p in pts]

    def fn(n, c):
        return surf if top is not None and n.z > 0.985 and abs(c.z - top) < 0.002 else brk
    return hull_mesh(pts, fn)


def mk_crumb(rng, a, flat_top=0.4, rebar=False):
    """Бетонная крошка ~a: оболочка точек в сплющенном бруске; верх иногда — исходная поверхность (опалубка);
    rebar — обрезок арматуры Ø12 из излома, отогнут вверх."""
    pts = [Vector((rng.uniform(-1, 1) * a, rng.uniform(-0.8, 0.8) * a, rng.uniform(-0.5, 0.5) * a * rng.uniform(0.55, 1.0)))
           for _ in range(rng.randint(7, 11))]
    pts, h = flat_bottomed(pts, 0.18)
    top = None
    if rng.random() < flat_top:
        top = h * rng.uniform(0.75, 0.9)
        pts = [Vector((p.x, p.y, min(p.z, top))) for p in pts]

    def fn(n, c):
        return 'concrete' if top is not None and n.z > 0.985 and abs(c.z - top) < 0.002 else 'broken'
    m = hull_mesh(pts, fn)
    if rebar:
        xm = max(p.x for p in pts)
        z = (top if top is not None else h) * 0.45
        yy = rng.uniform(-0.25, 0.25) * a
        p1 = Vector((xm + 0.01, yy, z))
        up = math.radians(rng.uniform(12, 32))
        side = rng.uniform(-0.35, 0.35)
        l1 = rng.uniform(0.06, 0.1)
        l2 = rng.uniform(0.06, 0.13)
        p2 = p1 + Vector((math.cos(side) * math.cos(up * 0.3), math.sin(side), math.sin(up * 0.3))) * l1
        bend = side + rng.uniform(-0.6, 0.6)
        p3 = p2 + Vector((math.cos(bend) * math.cos(up), math.sin(bend) * math.cos(up), math.sin(up))) * l2
        tube(m, [Vector((xm - 0.05, yy, z)), p1, p2, p3], 0.006, 4, 'rebar', cap0=False, cap1=True,
             roll=rng.uniform(0, 1))
    return m


# ------------------------------------------------------------------ шифер (волна в геометрии)

def arc_fn(phase, amp=AMP, pitch=PITCH, length=0.6, step=0.0005):
    """Длина дуги профиля от 0 до u (UV без растяжения по волне)."""
    us = np.arange(0.0, length + 0.05, step)
    dz = -amp * 2 * math.pi / pitch * np.sin(2 * math.pi * (us + phase) / pitch)
    s = np.concatenate([[0.0], np.cumsum(np.sqrt(1 + dz[:-1] ** 2) * step)])
    return lambda u: float(np.interp(u, us, s))


def corrugated(rng, nu, wv, nv, phase, ragged=('u0', 'u1', 'v0', 'v1'), jit=0.38, corners=0.5, sag=0.0,
               top='slate', edge='slate_edge', skirt=-0.003):
    """Кусок волнистого листа: сетка nu × nv (по волне 4 сегмента на период: гребень, склон, впадина, склон), край —
    неровный (вершины границы уведены внутрь), юбка от верха до z = skirt (толщина листа и тень под гребнем; низа нет —
    лист лежит впадинами на земле). Возвращает DMesh с атрибутом sheet = (u + phase, v, 0)."""
    du = PITCH / 4
    wu = nu * du
    dv = wv / nv
    arc = arc_fn(phase, length=wu)
    m = DMesh()
    P = {}
    for i in range(nu + 1):
        for j in range(nv + 1):
            u, v = i * du, j * dv
            if i == 0 and 'u0' in ragged and 0 < j < nv:
                u += rng.uniform(0.0, jit) * du
            if i == nu and 'u1' in ragged and 0 < j < nv:
                u -= rng.uniform(0.0, jit) * du
            if j == 0 and 'v0' in ragged and 0 < i < nu:
                v += rng.uniform(0.0, jit) * dv
            if j == nv and 'v1' in ragged and 0 < i < nu:
                v -= rng.uniform(0.0, jit) * dv
            corner = i in (0, nu) and j in (0, nv)
            if corner and rng.random() < corners:
                su = 1 if i == 0 else -1
                sv = 1 if j == 0 else -1
                t = rng.uniform(0.15, 0.42)
                u += su * t * du
                v += sv * t * dv
            P[i, j] = (u, v)
    top_id = {}
    for (i, j), (u, v) in P.items():
        z = AMP * (1 + math.cos(2 * math.pi * (u + phase) / PITCH)) + SLATE_T + sag * math.sin(math.pi * v / wv)
        top_id[i, j] = m.v((u, v, z))
        m.vattr.setdefault('sheet', {})[top_id[i, j]] = (u + phase, v, 0.0)
    for i in range(nu):
        for j in range(nv):
            q = [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)]
            m.f([top_id[k] for k in q], top, uv=[(arc(P[k][0]), P[k][1]) for k in q], expect=UP)
    loop = [(i, 0) for i in range(nu)] + [(nu, j) for j in range(nv)] + [(i, nv) for i in range(nu, 0, -1)] + \
           [(0, j) for j in range(nv, 0, -1)]
    bot = {}
    for k in loop:
        u, v = P[k]
        bot[k] = m.v((u, v, skirt))
        m.vattr['sheet'][bot[k]] = (u + phase, v, 0.0)
    s = 0.0
    for n in range(len(loop)):
        a, b = loop[n], loop[(n + 1) % len(loop)]
        pa, pb = m.verts[top_id[a]], m.verts[top_id[b]]
        ds = (pb - pa).length
        e = pb - pa
        out = Vector((e.y, -e.x, 0.0))
        m.f([top_id[a], bot[a], bot[b], top_id[b]], edge,
            uv=[(s, pa.z), (s, skirt), (s + ds, skirt), (s + ds, pb.z)], expect=out)
        s += ds
    return m


# ------------------------------------------------------------------ доски, рейки, щепа

def ring_pts(x, y0, y1, z0, z1, dy=0.0, twist=0.0):
    cy, cz = (y0 + y1) / 2, (z0 + z1) / 2
    c, s = math.cos(twist), math.sin(twist)
    out = []
    for yy, zz in ((y0, z0), (y1, z0), (y1, z1), (y0, z1)):
        ry, rz = yy - cy, zz - cz
        out.append(Vector((x, cy + dy + ry * c - rz * s, cz + ry * s + rz * c)))
    return out


SIDES = ((0, 1, Vector((0, 0, -1))), (1, 2, Vector((0, 1, 0))), (2, 3, Vector((0, 0, 1))), (3, 0, Vector((0, -1, 0))))


def board_rings(m, xs, y0, y1, T, bow, twist, bottom, alias, inner=None, x_len=None, matrix=None, taper_inner=0.0):
    """Брус вдоль X: кольца по xs (изгиб bow по Y, кручение twist), грани боков; inner — сторона ('+y'/'-y') расщепа
    (её псевдоним wood_split, она сужается к концу на taper_inner ширины)."""
    rings = []
    L = x_len or (xs[-1] - xs[0])
    for x in xs:
        t = (x - xs[0]) / L if L > 0 else 0.0
        ya, yb = y0, y1
        if inner == '+y':
            yb = y1 - (y1 - y0) * taper_inner * t
        elif inner == '-y':
            ya = y0 + (y1 - y0) * taper_inner * t
        pts = ring_pts(x, ya, yb, 0.0, T, bow * math.sin(math.pi * t), twist * (t - 0.5))
        if matrix is not None:
            pts = [matrix @ p for p in pts]
        rings.append([m.v(p) for p in pts])
    for i in range(len(rings) - 1):
        for a, b, nrm in SIDES:
            if a == 0 and not bottom:
                continue
            al = alias
            if (inner == '+y' and a == 1) or (inner == '-y' and a == 3):
                al = 'wood_split'
            ex = (matrix.to_3x3() @ nrm) if matrix is not None else nrm
            m.f([rings[i][a], rings[i][b], rings[i + 1][b], rings[i + 1][a]], al, expect=ex)
    return rings


def board_end(m, ring, direction, kind, rng, bottom, size, jag=0.02):
    """Торец: 'sawn' — ровный срез; 'broken' — углы вразнобой и зубец-остриё."""
    pts = [m.verts[i] for i in ring]
    axis = Vector((direction, 0.0, 0.0))
    if kind == 'sawn':
        m.f(ring, 'wood_split', expect=axis)
        return
    for i in ring:
        m.verts[i] = m.verts[i] + axis * rng.uniform(-0.6, 0.6) * jag
    pts = [m.verts[i] for i in ring]
    c = sum(pts, Vector()) / 4
    w, t = size
    tip = m.v(c + axis * rng.uniform(0.025, 0.07) * max(1.0, w / 0.05) + Vector((0.0, rng.uniform(-0.3, 0.3) * w,
                                                                            rng.uniform(-0.25, 0.25) * t)))
    for a, b, nrm in SIDES:
        if a == 0 and not bottom:
            continue
        tri = [ring[a], ring[b], tip]
        cc = (m.verts[ring[a]] + m.verts[ring[b]] + m.verts[tip]) / 3
        m.f(tri, 'wood_split', expect=cc - c)


def mk_board(rng, L, W, T, segs=1, ends=('broken', 'broken'), bow=0.0, twist=0.0, bottom=False, split=None, nails=0):
    """Доска/рейка/брус вдоль X (x ∈ [-L/2, L/2], низ z = 0). split = dict(at, lengths, angles (°), segs, taper) —
    конец +X расщеплён по волокну на два зубца, расходящихся в плоскости XY; nails — ржавые гвозди сверху."""
    m = DMesh()
    x_end = L / 2 if split is None else -L / 2 + L * split['at']
    xs = [-L / 2 + (x_end + L / 2) * i / segs for i in range(segs + 1)]
    rings = board_rings(m, xs, -W / 2, W / 2, T, bow, twist, bottom, 'wood', x_len=L)
    board_end(m, rings[0], -1, ends[0], rng, bottom, (W, T))
    if split is None:
        board_end(m, rings[-1], 1, ends[1], rng, bottom, (W, T))
    else:
        t_end = (x_end + L / 2) / L
        dy_end = bow * math.sin(math.pi * t_end)
        yc = -W / 2 + W * rng.uniform(0.4, 0.6)
        for (y0, y1, inner), ln, ang in zip(((-W / 2, yc, '+y'), (yc, W / 2, '-y')), split['lengths'], split['angles']):
            ps = split.get('segs', 2)
            pxs = [x_end - 0.015 + (ln + 0.015) * i / ps for i in range(ps + 1)]
            piv = Vector((x_end, yc + dy_end, 0.0))
            mat = Matrix.Translation(piv) @ Matrix.Rotation(math.radians(ang), 4, 'Z') @ Matrix.Translation(-piv) @ \
                Matrix.Translation((0.0, dy_end, 0.0))
            pr = board_rings(m, pxs, y0, y1, T, 0.0, 0.0, bottom, 'wood', inner=inner, matrix=mat,
                             taper_inner=split.get('taper', 0.3))
            board_end(m, pr[-1], 1, 'broken', rng, bottom, (y1 - y0, T), jag=0.03)
    for k in range(nails):
        x = rng.uniform(-0.45, 0.4) * L
        y = rng.uniform(-0.3, 0.3) * W
        h = rng.uniform(0.025, 0.05)
        a = rng.uniform(0, 2 * math.pi)
        bent = Vector((math.cos(a), math.sin(a), 0.0)) * rng.uniform(0.01, 0.03)
        tube(m, [Vector((x, y, T - 0.012)), Vector((x, y, T + h)), Vector((x, y, T + h)) + bent + Vector((0, 0, 0.004))],
             0.0022, 3, 'rebar', cap0=False, cap1=True)
    return m


def mk_splinter(rng, L, w, t, weathered=0.5):
    """Щепа: клин-оболочка, тупой конец рваный, остриё сбоку."""
    pts = [Vector((rng.uniform(-0.012, 0.012), -w / 2, 0.0)), Vector((rng.uniform(-0.012, 0.012), w / 2, 0.0)),
           Vector((rng.uniform(-0.012, 0.012), -w * 0.4, t)), Vector((rng.uniform(-0.012, 0.012), w * 0.4, t)),
           Vector((L * 0.55, -w * 0.35 + rng.uniform(-0.15, 0.15) * w, 0.0)), Vector((L * 0.55, w * 0.35, 0.0)),
           Vector((L * 0.55, rng.uniform(-0.2, 0.2) * w, t * 0.85)), Vector((L, rng.uniform(-0.3, 0.3) * w, t * 0.25))]
    pts = [p - Vector((L / 2, 0.0, 0.0)) for p in pts]
    top = 'wood' if rng.random() < weathered else 'wood_split'
    return hull_mesh(pts, lambda n, c: top if n.z > 0.9 else 'wood_split')


# ------------------------------------------------------------------ плиты, обломки бетона

def prism(m, outline, flags, T, rng, top='concrete', side_broken='broken', side_orig='concrete', bottom=None, bands=2,
          wobble=0.015, zjit=0.012):
    """Призма по контуру outline (CCW, z = 0..T); flags[k] — ребро k (k -> k+1) — излом: его вершины гуляют по
    высоте верха и вбок по поясу середины (излом не плоский)."""
    n = len(outline)
    rings = []
    for k, p in enumerate(outline):
        brk = flags[k] or flags[k - 1]
        e0 = outline[k] - outline[k - 1]
        e1 = outline[(k + 1) % n] - outline[k]
        out = Vector((e0.y, -e0.x, 0.0)).normalized() + Vector((e1.y, -e1.x, 0.0)).normalized()
        out = out.normalized() if out.length > 1e-6 else Vector((0.0, 0.0, 0.0))
        ring = [m.v((p.x, p.y, 0.0))]
        for b in range(1, bands):
            zz = T * b / bands + (rng.uniform(-0.2, 0.2) * T / bands if brk else 0.0)
            off = out * rng.uniform(-wobble, wobble) if brk else Vector((0.0, 0.0, 0.0))
            ring.append(m.v((p.x + off.x, p.y + off.y, zz)))
        ring.append(m.v((p.x, p.y, T + (rng.uniform(-zjit, zjit * 0.4) if brk else 0.0))))
        rings.append(ring)
    m.f([r[-1] for r in rings], top, expect=UP)
    if bottom:
        m.f([r[0] for r in rings], bottom, expect=-UP)
    for k in range(n):
        k2 = (k + 1) % n
        e = outline[k2] - outline[k]
        out = Vector((e.y, -e.x, 0.0))
        al = side_broken if flags[k] else side_orig
        for lv in range(len(rings[k]) - 1):
            m.f([rings[k][lv], rings[k2][lv], rings[k2][lv + 1], rings[k][lv + 1]], al, expect=out)
    return rings


def jag(rng, a, b, seg, amp):
    """Точки строго между a и b через ~seg, сдвинутые поперёк на ±amp."""
    d = b - a
    L = d.length
    n = max(1, int(round(L / seg)))
    side = Vector((-d.y, d.x, 0.0)).normalized()
    return [a + d * (s / n) + side * rng.uniform(-amp, amp) + d.normalized() * rng.uniform(-0.2, 0.2) * L / n
            for s in range(1, n)]


# ================================================================== ассеты

def place_brick(asset, pile, area, rng, what, kind, soot=0.0):
    edge = rng.random() < 0.25
    if what == 'whole':
        m = mk_brick(rng, kind, 0.25, chips=rng.randint(0, 2), edge=edge)
    elif what == 'half':
        m = mk_brick(rng, kind, rng.uniform(0.11, 0.14), broken=('+',), chips=rng.randint(0, 1), edge=edge)
    else:
        m = mk_brick(rng, kind, rng.uniform(0.055, 0.09), broken=('+', '-'), edge=edge)
    return drop_piece(asset, pile, m, rng, area, sink=0.006, jitter=2.5, max_height=0.16, soot=soot)


def fill(asset, pile, area, rng, maker, target, hi, sink=0.004, smooth=None, auto_uv=True, max_height=0.16):
    misses = 0
    while asset.tris() < target and misses < 30:
        m, soot = maker()
        if asset.tris() + m.tris() > hi:
            misses += 1
            continue
        if drop_piece(asset, pile, m, rng, area, sink=sink, jitter=3.0, max_height=max_height, soot=soot,
                      smooth=smooth, auto_uv=auto_uv) is None:
            misses += 1


def build_bricks_a(asset, rng):
    area = Disc(0.42, rng, clusters=2)
    pile = Pile()
    big = [('whole', 'red'), ('whole', 'red'), ('whole', 'sil'), ('half', 'red'), ('half', 'red'), ('half', 'red'),
           ('half', 'sil'), ('frag', 'red'), ('frag', 'red'), ('frag', 'sil')]
    rng.shuffle(big)
    for k, (what, kind) in enumerate(big):
        place_brick(asset, pile, area, rng, what, kind, soot=0.45 if k == 4 else 0.0)

    def chip():
        kind = 'red' if rng.random() < 0.65 else 'sil'
        return mk_chip(rng, kind, rng.uniform(0.022, 0.06)), (0.4 if rng.random() < 0.06 else 0.0)
    fill(asset, pile, area, rng, chip, 560, asset.budget[1] - 10)


def build_bricks_b(asset, rng):
    area = Disc(0.44, rng, clusters=3, sigma=0.13)
    pile = Pile()
    big = [('whole', 'sil'), ('half', 'red'), ('half', 'red'), ('half', 'sil'), ('half', 'sil'), ('frag', 'red'),
           ('frag', 'red'), ('frag', 'sil'), ('frag', 'sil')]
    rng.shuffle(big)
    for what, kind in big:
        place_brick(asset, pile, area, rng, what, kind)

    def chip():
        kind = 'red' if rng.random() < 0.55 else 'sil'
        return mk_chip(rng, kind, rng.uniform(0.015, 0.045)), (0.4 if rng.random() < 0.06 else 0.0)
    fill(asset, pile, area, rng, chip, 640, asset.budget[1] - 10)


def build_concrete_bits(asset, rng):
    area = Disc(0.40, rng, clusters=2)
    pile = Pile()
    for _ in range(2):
        drop_piece(asset, pile, mk_crumb(rng, rng.uniform(0.075, 0.1), flat_top=1.0, rebar=True), rng, area,
                   sink=0.008, jitter=4.0, max_height=0.2)
    for _ in range(3):
        drop_piece(asset, pile, mk_crumb(rng, rng.uniform(0.035, 0.055), flat_top=0.6), rng, area, sink=0.006,
                   jitter=4.0, max_height=0.16)

    def crumb():
        return mk_crumb(rng, rng.uniform(0.012, 0.035), flat_top=0.3), 0.0
    fill(asset, pile, area, rng, crumb, 560, asset.budget[1] - 10)


def build_slate_shards(asset, rng):
    area = Disc(0.42, rng, clusters=2, sigma=0.12)
    pile = Pile()
    first = [(10, 0.26), (8, 0.3), (7, 0.2), (5, 0.16), (4, 0.22), (4, 0.12)]

    def shard(nu=None, wv=None):
        nu = nu or rng.randint(2, 6)
        wv = wv or rng.uniform(0.08, 0.2)
        nv = 1 if wv < 0.15 else 2
        ragged = tuple(s for s in ('u0', 'u1', 'v0', 'v1') if rng.random() < 0.8)
        m = corrugated(rng, nu, wv, nv, phase=rng.randint(0, 3) * PITCH / 4, ragged=ragged, jit=0.38, corners=0.6)
        return m
    for nu, wv in first:
        drop_piece(asset, pile, shard(nu, wv), rng, area, sink=0.003, jitter=3.0, max_height=0.2, smooth=60.0,
                   auto_uv=False)

    def more():
        return shard(), 0.0
    fill(asset, pile, area, rng, more, 430, asset.budget[1] - 10, sink=0.003, smooth=60.0, auto_uv=False,
         max_height=0.2)


def build_wood_bits(asset, rng):
    area = Disc(0.42, rng, clusters=2, sigma=0.13)
    pile = Pile()
    sections = [(0.045, 0.022), (0.04, 0.02), (0.05, 0.025), (0.038, 0.018)]
    for k in range(7):
        w, t = rng.choice(sections)
        L = rng.uniform(0.18, 0.5)
        ends = ('broken', 'broken') if rng.random() < 0.6 else ('sawn', 'broken')
        m = mk_board(rng, L, w, t, segs=1 if L < 0.3 else 2, ends=ends, bow=rng.uniform(-0.006, 0.006),
                     twist=rng.uniform(-0.05, 0.05), nails=1 if k == 2 else 0)
        drop_piece(asset, pile, m, rng, area, sink=0.004, jitter=3.0, max_height=0.12, soot=0.5 if k == 5 else 0.0)

    def splinter():
        return mk_splinter(rng, rng.uniform(0.06, 0.24), rng.uniform(0.008, 0.022), rng.uniform(0.003, 0.008)), 0.0
    fill(asset, pile, area, rng, splinter, 330, asset.budget[1] - 10, sink=0.002, max_height=0.12)


def build_brick_chunk(asset, rng):
    """Кусок кладки в 1 кирпич (250 мм): ячейки в полкирпича × курс; ступени перевязки по краям, выпавший кирпич, рваный
    верх тыльной версты; грани одной плоскости слиты. Плоскость y = 0 (лицо стены) — со штукатуркой; лежит лицом вверх."""
    nx, ny, nz = 6, 2, 4
    CX, CY, CZ = 0.125, 0.125, 0.075
    occ = np.ones((nx, ny, nz), bool)
    starts = [0, rng.randint(0, 1), 1, 2]
    ends = [nx, nx - rng.randint(0, 1), nx - 1, nx - 1 - rng.randint(0, 1)]
    for k in range(nz):
        occ[:starts[k], :, k] = False
        occ[ends[k]:, :, k] = False
    i0 = rng.randint(starts[1] + 1, max(starts[1] + 1, ends[1] - 3))
    occ[i0:i0 + 2, 0, 1] = False                       # выпавший кирпич лица
    occ[ends[3] - 1:, 1, 3] = False                     # тыльная верста сверху
    occ[:starts[2] + 1, 1, 2] = False
    m = DMesh()
    vid = {}

    def V(i, j, k):
        if (i, j, k) not in vid:
            vid[i, j, k] = m.v((i * CX, j * CY, k * CZ))
        return vid[i, j, k]
    dirs = [((-1, 0, 0), [(0, 0, 0), (0, 1, 0), (0, 1, 1), (0, 0, 1)]), ((1, 0, 0), [(1, 0, 0), (1, 0, 1), (1, 1, 1), (1, 1, 0)]),
            ((0, -1, 0), [(0, 0, 0), (0, 0, 1), (1, 0, 1), (1, 0, 0)]), ((0, 1, 0), [(0, 1, 0), (1, 1, 0), (1, 1, 1), (0, 1, 1)]),
            ((0, 0, -1), [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)]), ((0, 0, 1), [(0, 0, 1), (0, 1, 1), (1, 1, 1), (1, 0, 1)])]
    for i in range(nx):
        for j in range(ny):
            for k in range(nz):
                if not occ[i, j, k]:
                    continue
                for d, quad in dirs:
                    a, b, c = i + d[0], j + d[1], k + d[2]
                    if 0 <= a < nx and 0 <= b < ny and 0 <= c < nz and occ[a, b, c]:
                        continue
                    alias = 'brickwork_plaster' if d == (0, -1, 0) and j == 0 else 'brickwork'
                    m.f([V(i + q[0], j + q[1], k + q[2]) for q in quad], alias, expect=Vector(d))
    m = merge_coplanar(m)
    centre = Vector((nx * CX / 2, ny * CY / 2, nz * CZ / 2))
    m.translate(-centre)
    for i in range(len(m.verts)):
        m.verts[i] = m.verts[i] + Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * 0.003
    pile = Pile()
    # лицом вверх (лицо — -Y материальной системы), сдвиг шума кратен кирпичу и двум курсам: рисунок кладки по швам
    drop_piece(asset, pile, m, rng, None, sink=0.02, jitter=4.0, base=Matrix.Rotation(math.radians(-90.0), 4, 'X'),
               xy=(0.0, 0.0), offset=Vector((10.0, 0.0, 4.5)))


def build_concrete_chunk(asset, rng):
    T = 0.13
    n = 12
    pts = []
    for k in range(n):
        a = 2 * math.pi * k / n + rng.uniform(-0.12, 0.12)
        r = rng.uniform(0.82, 1.12)
        pts.append(Vector((0.36 * r * math.cos(a), 0.27 * r * math.sin(a), 0.0)))
    k0 = rng.randrange(n)
    a, c = pts[k0], pts[(k0 + 2) % n]
    pts[(k0 + 1) % n] = (a + c) / 2                     # прямой исходный край (опалубка)
    flags = [True] * n
    flags[k0] = flags[(k0 + 1) % n] = False
    m = DMesh()
    prism(m, pts, flags, T, rng, bands=2, wobble=0.018)
    # арматура: две параллельные из одного излома, одна из другого
    def edge_bar(k, off, z):
        p, q = pts[k], pts[(k + 1) % n]
        e = q - p
        out = Vector((e.y, -e.x, 0.0)).normalized()
        base = p + e * (0.5 + off) - out * 0.06
        l1 = rng.uniform(0.12, 0.2)
        p1 = base + out * 0.07
        p2 = p1 + out * l1 + Vector((0, 0, rng.uniform(-0.01, 0.03)))
        bend = Matrix.Rotation(rng.uniform(-0.7, 0.7), 3, 'Z') @ out
        p3 = p2 + bend * rng.uniform(0.08, 0.16) + Vector((0, 0, rng.uniform(0.0, 0.06)))
        tube(m, [base + Vector((0, 0, z)), p1 + Vector((0, 0, z)), p2 + Vector((0, 0, z)), p3 + Vector((0, 0, z))],
             0.006, 4, 'rebar', cap0=False, cap1=True, roll=rng.uniform(0, 1))
    kb = [k for k in range(n) if flags[k]]
    k1 = rng.choice(kb)
    edge_bar(k1, -0.12, 0.04)
    edge_bar(k1, 0.14, 0.04)
    edge_bar(rng.choice([k for k in kb if k != k1]), 0.0, T - 0.035)
    pile = Pile()
    drop_piece(asset, pile, m, rng, None, sink=0.02, jitter=5.0, xy=(0.0, 0.0))


def build_slate_sheet(asset, rng):
    m = corrugated(rng, 18, 1.1, 3, phase=0.0, ragged=('v1', 'u1'), jit=0.42, corners=0.4, sag=0.022)
    m.translate(Vector((-18 * PITCH / 8, -0.55, 0.0)))
    pile = Pile()
    drop_piece(asset, pile, m, rng, None, sink=0.003, jitter=1.0, xy=(0.0, 0.0), smooth=60.0, auto_uv=False)


def build_planks(asset, rng):
    pile = Pile()
    a = mk_board(rng, 2.2, 0.15, 0.025, segs=6, ends=('sawn', None), bow=0.012, twist=0.03, bottom=False, nails=2,
                 split=dict(at=0.78, lengths=(0.36, 0.52), angles=(-3.0, 4.5), segs=2, taper=0.35))
    c = mk_board(rng, 1.6, 0.10, 0.022, segs=5, ends=('broken', 'broken'), bow=0.01, twist=0.02, bottom=False)
    b = mk_board(rng, 1.8, 0.05, 0.05, segs=5, ends=('broken', 'sawn'), bow=0.008, twist=0.0, bottom=True, nails=1)
    drop_piece(asset, pile, a, rng, None, sink=0.004, jitter=1.0, xy=(0.0, 0.0), yaw=0.18)
    drop_piece(asset, pile, c, rng, None, sink=0.004, jitter=1.0, xy=(0.15, 0.62), yaw=-0.12, soot=0.3)
    drop_piece(asset, pile, b, rng, None, sink=0.002, jitter=0.5, xy=(0.05, 0.3), yaw=1.2, keep_bottom=True)


def build_beam(asset, rng):
    pile = Pile()
    m = mk_board(rng, 3.0, 0.15, 0.05, segs=7, ends=('sawn', None), bow=0.03, twist=0.04, bottom=False, nails=2,
                 split=dict(at=0.8, lengths=(0.45, 0.68), angles=(-4.0, 6.0), segs=3, taper=0.35))
    drop_piece(asset, pile, m, rng, None, sink=0.012, jitter=1.0, xy=(0.0, 0.0), yaw=0.1, soot=0.25)
    for k in range(3):
        s = mk_splinter(rng, rng.uniform(0.2, 0.42), rng.uniform(0.015, 0.03), rng.uniform(0.006, 0.012), weathered=0.3)
        drop_piece(asset, pile, s, rng, None, sink=0.003, jitter=4.0, xy=(1.25 + rng.uniform(0.0, 0.5),
                                                                         rng.uniform(-0.3, 0.3)))


def build_iron_sheet(asset, rng):
    """Лист 1.0 × 2.0 м: дальний конец закручен вверх, косой залом поперёк, отогнутый угол, лёгкая волнистость; верх и
    низ — свои грани (низ на 2 мм ниже по нормали, оцинковка); атрибут sheet = (u, v, сторона) — волна в нормали."""
    WU, WV = 1.0, 2.0
    us = [WU * i / 10 for i in range(11)]
    vs = [0.0, 0.25, 0.48, 0.62, 0.74, 0.86, 1.0, 1.25, 1.5, 1.76, 2.0]
    ph = rng.uniform(0, 2 * math.pi)

    def z_of(u, v):
        z = 0.30 * max(0.0, (v - 1.42) / 0.58) ** 2.2
        d = v - 0.74 - 0.25 * (u - 0.5)
        z += 0.075 * math.exp(-(d / 0.11) ** 2) * (0.7 + 0.3 * math.sin(u * 5.0 + ph))
        z += 0.022 * math.sin(v * 3.1 + ph) * (0.4 + u)
        z += 0.16 * max(0.0, (u - 0.55) / 0.45) ** 2 * max(0.0, (0.45 - v) / 0.45) ** 1.5
        return z
    P = {}
    nu, nv = len(us) - 1, len(vs) - 1
    for i, u in enumerate(us):
        for j, v in enumerate(vs):
            uu = u + (rng.uniform(-0.006, 0.006) if i in (0, nu) else 0.0)
            vv = v + (rng.uniform(-0.006, 0.006) if j in (0, nv) else 0.0)
            P[i, j] = (uu, vv)
    P[nu, 0] = (WU - 0.05, 0.1)                        # сорванный угол
    m = DMesh()
    top, bot = {}, {}
    e = 0.004
    for k, (u, v) in P.items():
        z = z_of(u, v)
        n = Vector((-(z_of(u + e, v) - z_of(u - e, v)) / (2 * e), -(z_of(u, v + e) - z_of(u, v - e)) / (2 * e), 1.0)).normalized()
        p = Vector((u - WU / 2, v - WV / 2, z))
        top[k] = m.v(p)
        bot[k] = m.v(p - n * 0.002)
        m.vattr.setdefault('sheet', {})[top[k]] = (u, v, 1.0)
        m.vattr['sheet'][bot[k]] = (u, v, -1.0)
    for i in range(nu):
        for j in range(nv):
            q = [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)]
            uv = [P[k] for k in q]
            nrm = newell([m.verts[top[k]] for k in q])
            m.f([top[k] for k in q], 'iron_top', uv=uv, expect=nrm if nrm.z > 0 else UP)
            m.f([bot[k] for k in reversed(q)], 'iron_bottom', uv=list(reversed(uv)), expect=-(nrm if nrm.z > 0 else UP))
    pile = Pile()
    drop_piece(asset, pile, m, rng, None, sink=0.004, jitter=0.0, xy=(0.0, 0.0), yaw=0.35, smooth=70.0,
               auto_uv=False, keep_bottom=True)


def build_slab(asset, rng):
    """Обломок плиты перекрытия 2.2 × 1.3 м, 160 мм, треснувший надвое: половина A лежит, B откинута по трещине на 4.5°;
    четыре стержня нижней сетки держат их через щель, из изломов торчат отогнутые стержни."""
    T = 0.16
    X0, X1, Y0, Y1 = -1.12, 1.08, -0.66, 0.66
    gap = 0.045
    ny = 8
    crack = []
    for j in range(ny + 1):
        y = Y0 + (Y1 - Y0) * j / ny
        x = 0.18 + 0.16 * (j / ny - 0.5) + (rng.uniform(-0.045, 0.045) if 0 < j < ny else 0.0)
        crack.append(Vector((x, y, 0.0)))

    def crack_x(y):
        return float(np.interp(y, [c.y for c in crack], [c.x for c in crack]))
    # половина A (CCW): перед (исходный) -> трещина вверх -> тыл (излом) -> левый край (исходный)
    back_a = jag(rng, crack[-1], Vector((X0, Y1, 0.0)), 0.14, 0.05)
    A = [Vector((X0, Y0, 0.0))] + crack + back_a + [Vector((X0, Y1, 0.0))]
    fa = [False] + [True] * (len(crack) - 1) + [True] * (len(back_a) + 1) + [False]
    # половина B (CCW): перед (исходный) -> правый излом -> тыл -> трещина вниз
    X1r = X1 - 0.07
    right = jag(rng, Vector((X1, Y0, 0.0)), Vector((X1r, Y1, 0.0)), 0.12, 0.05)
    back_b = jag(rng, Vector((X1r, Y1, 0.0)), Vector((crack[-1].x + gap, Y1, 0.0)), 0.14, 0.05)
    cr_b = [Vector((c.x + gap, c.y, 0.0)) for c in reversed(crack)]
    B = [cr_b[-1], Vector((X1, Y0, 0.0))] + right + [Vector((X1r, Y1, 0.0))] + back_b + cr_b[:-1]
    fb = [False] + [True] * (len(B) - 1)
    zb = 0.04
    ma = DMesh()
    prism(ma, A, fa, T, rng, bands=2, wobble=0.02, zjit=0.015)
    for x in (-0.82, -0.38):
        y_e = Y1
        p0 = Vector((x, y_e - 0.1, zb))
        p1 = Vector((x + rng.uniform(-0.02, 0.02), y_e + 0.07, zb))
        p2 = p1 + Vector((rng.uniform(-0.08, 0.08), rng.uniform(0.1, 0.18), 0.02))
        p3 = p2 + Vector((rng.uniform(-0.1, 0.1), rng.uniform(0.04, 0.1), 0.04))
        tube(ma, [p0, p1, p2, p3], 0.006, 4, 'rebar', cap0=False, cap1=True, roll=rng.uniform(0, 1))
    mb = DMesh()
    prism(mb, B, fb, T, rng, bands=2, wobble=0.02, zjit=0.015, bottom='ceiling')
    for y in (-0.42, 0.02, 0.4):
        p0 = Vector((X1 - 0.13, y, zb))
        p1 = Vector((X1 + rng.uniform(0.05, 0.12), y + rng.uniform(-0.02, 0.02), zb))
        p2 = p1 + Vector((rng.uniform(0.08, 0.17), rng.uniform(-0.06, 0.06), -0.025))
        p3 = p2 + Vector((rng.uniform(0.04, 0.09), rng.uniform(-0.06, 0.06), -0.01))
        tube(mb, [p0, p1, p2, p3], 0.006, 4, 'rebar', cap0=False, cap1=True, roll=rng.uniform(0, 1))
    xb = 0.65
    p0 = Vector((xb, Y1 - 0.12, T - 0.03))
    p1 = Vector((xb, Y1 + 0.04, T - 0.03))
    p2 = p1 + Vector((0.02, 0.1, 0.09))
    p3 = p2 + Vector((-0.03, 0.06, 0.15))
    tube(mb, [p0, p1, p2, p3], 0.006, 4, 'rebar', cap0=False, cap1=True)
    pose_a = Matrix.Translation((0.0, 0.0, -0.03)) @ Matrix.Rotation(math.radians(0.8), 4, Vector((0.3, 1.0, 0.0)).normalized())
    hinge0 = Vector((crack[0].x + gap, Y0, 0.0))
    hinge1 = Vector((crack[-1].x + gap, Y1, 0.0))
    pose_b = Matrix.Translation((0.0, 0.0, -0.03)) @ Matrix.Translation(hinge0) @ \
        Matrix.Rotation(-math.radians(4.5), 4, (hinge1 - hinge0).normalized()) @ Matrix.Translation(-hinge0)
    strip_bottom(ma, pose_a)
    asset.pieces.append(Piece(ma, pose_a))
    asset.pieces.append(Piece(mb, pose_b))
    bars = DMesh()
    for y in (-0.5, -0.17, 0.15, 0.48):
        y += rng.uniform(-0.04, 0.04)
        cx = crack_x(y)
        pa = pose_a @ Vector((cx - 0.08, y, zb))
        pb = pose_b @ Vector((cx + gap + 0.08, y, zb))
        m1 = pa.lerp(pb, 0.35) + Vector((0.0, rng.uniform(-0.01, 0.01), -0.008))
        m2 = pa.lerp(pb, 0.65) + Vector((0.0, rng.uniform(-0.01, 0.01), -0.006))
        tube(bars, [pa, m1, m2, pb], 0.006, 4, 'rebar', cap0=False, cap1=False, roll=rng.uniform(0, 1))
    asset.pieces.append(Piece(bars, Matrix.Identity(4), smooth=80.0))


ASSETS = [
    ('debris_bits_brick_a', 'small', (400, 700), build_bricks_a),
    ('debris_bits_brick_b', 'small', (400, 700), build_bricks_b),
    ('debris_bits_concrete', 'small', (400, 700), build_concrete_bits),
    ('debris_shards_slate', 'small', (300, 500), build_slate_shards),
    ('debris_bits_wood', 'small', (250, 400), build_wood_bits),
    ('debris_brick_chunk', 'medium', (60, 160), build_brick_chunk),
    ('debris_concrete_chunk', 'medium', (120, 260), build_concrete_chunk),
    ('debris_slate_sheet', 'medium', (150, 250), build_slate_sheet),
    ('debris_planks', 'medium', (150, 350), build_planks),
    ('debris_slab', 'large', (300, 600), build_slab),
    ('debris_iron_sheet', 'large', (250, 500), build_iron_sheet),
    ('debris_beam', 'large', (60, 250), build_beam),
]


# ================================================================== шейдеры (на основе ruinmats)

def make_nb_class():
    class DNB(rm.NB):
        """Нормаль для Ctx — в пространстве объекта (материальная система куска), а не мира."""

        def normal(self):
            g = self.node('ShaderNodeNewGeometry')
            vt = self.node('ShaderNodeVectorTransform', vector_type='NORMAL', convert_from='WORLD', convert_to='OBJECT')
            self.nt.links.new(g.outputs['Normal'], vt.inputs['Vector'])
            return self.vmath('NORMALIZE', vt.outputs['Vector'])
    return DNB


def obj_rand(nb):
    return nb.node('ShaderNodeObjectInfo').outputs['Random']


def sheet_coords(nb):
    n = nb.node('ShaderNodeAttribute', attribute_name='sheet', attribute_type='GEOMETRY')
    return nb.sep(n.outputs['Vector'])


def k_brick_unit(nb, g, p):
    """Отдельный кирпич: цвет обжига (свой у каждого куска), песок и поры, остатки раствора."""
    tone = nb.mixc(obj_rand(nb), p.get('brick_a', (0.43, 0.19, 0.13)), p.get('brick_b', (0.33, 0.15, 0.10)))
    col = nb.scalec(tone, nb.mapr(nb.noise(g.pos, scale=7.0, detail=4.0), 0.3, 0.7, 0.86, 1.12))
    fine = nb.noise(g.pos, scale=80.0, detail=3.0)
    col = nb.scalec(col, nb.mapr(fine, 0.25, 0.75, 0.9, 1.08))
    pores = nb.mapr(nb.voronoi(g.pos, scale=140.0), 0.0, 0.14, 1.0, 0.0)
    col = nb.mixc(nb.math('MULTIPLY', pores, 0.35), col, nb.scalec(tone, 0.5))
    mort = nb.noise(g.pos, scale=6.0, detail=5.0, rough=0.62, distortion=0.5)
    mf = nb.mapr(mort, 0.55, 0.59, 0.0, 1.0)
    col = nb.mixc(mf, col, nb.scalec(p.get('mortar', (0.50, 0.48, 0.44)), nb.mapr(fine, 0.25, 0.75, 0.82, 1.1)))
    h = nb.math('ADD', nb.math('MULTIPLY', fine, 0.35), nb.math('MULTIPLY', mf, 0.4))
    h = nb.math('SUBTRACT', h, nb.math('MULTIPLY', pores, 0.25))
    return rm._finish_color(nb, g, col, p), h, 0.95


def k_brick_frac(nb, g, p):
    """Излом кирпича: пористая глина, светлые зёрна шамота/песка, ямки."""
    base = p.get('base', (0.47, 0.21, 0.14))
    n1 = nb.noise(g.pos, scale=30.0, detail=5.0, rough=0.6)
    col = nb.scalec(base, nb.mapr(n1, 0.3, 0.7, 0.8, 1.15))
    grains = nb.mapr(nb.voronoi(g.pos, scale=260.0), 0.0, 0.16, 1.0, 0.0)
    col = nb.mixc(nb.math('MULTIPLY', grains, 0.45), col, p.get('grain', (0.62, 0.42, 0.30)))
    pits = nb.mapr(nb.voronoi(g.pos, scale=90.0), 0.0, 0.1, 1.0, 0.0)
    col = nb.mixc(nb.math('MULTIPLY', pits, 0.4), col, nb.scalec(base, 0.45))
    h = nb.math('SUBTRACT', nb.math('ADD', nb.math('MULTIPLY', n1, 0.8), nb.math('MULTIPLY', grains, 0.2)),
                nb.math('MULTIPLY', pits, 0.4))
    return rm._finish_color(nb, g, col, p, soot_amt=0.5), h, 0.97


def k_slate(nb, g, p):
    """Асбестоцементный шифер: серый, гребни выветрены светлее, грязь в впадинах полосами вдоль волны, мох, лишайник."""
    base = p.get('base', (0.40, 0.40, 0.38))
    su, sv, _ = sheet_coords(nb)
    crest = nb.mapr(nb.math('COSINE', nb.math('MULTIPLY', su, 2 * math.pi / PITCH)), -1.0, 1.0, 0.0, 1.0)
    col = nb.mixc(nb.noise(g.pos, scale=1.8, detail=5.0), nb.scalec(base, 0.82), nb.scalec(base, 1.12))
    fib = nb.noise(g.pos, scale=60.0, detail=3.0)
    col = nb.scalec(col, nb.mapr(fib, 0.25, 0.75, 0.92, 1.06))
    col = nb.scalec(col, nb.mapr(crest, 0.0, 1.0, 0.84, 1.06))
    st = nb.noise(nb.comb(nb.math('MULTIPLY', su, 30.0), nb.math('MULTIPLY', sv, 2.0), 0.0), scale=1.0, detail=3.0)
    dirt = nb.math('MULTIPLY', nb.mapr(st, 0.5, 0.75, 0.0, 1.0), nb.mapr(crest, 0.4, 0.0, 0.0, 0.5))
    col = nb.mixc(dirt, col, nb.scalec(base, 0.55))
    moss = nb.mapr(nb.noise(g.pos, scale=3.2, detail=6.0, rough=0.7), 0.56, 0.72, 0.0, p.get('moss', 0.5))
    col = nb.mixc(moss, col, (0.24, 0.27, 0.17))
    lich = nb.mapr(nb.voronoi(g.pos, scale=14.0), 0.0, 0.07, p.get('lichen', 0.45), 0.0)
    lich = nb.math('MULTIPLY', lich, nb.mapr(nb.noise(g.pos, scale=4.0), 0.45, 0.6, 0.0, 1.0))
    col = nb.mixc(lich, col, (0.55, 0.55, 0.47))
    h = nb.math('ADD', nb.math('MULTIPLY', fib, 0.4), nb.math('MULTIPLY', moss, 0.3))
    return rm._finish_color(nb, g, col, p), h, 0.9


def k_slate_edge(nb, g, p):
    base = p.get('base', (0.55, 0.55, 0.52))
    n1 = nb.noise(g.pos, scale=90.0, detail=4.0)
    col = nb.scalec(base, nb.mapr(n1, 0.3, 0.7, 0.85, 1.1))
    return rm._finish_color(nb, g, col, p, soot_amt=0.3), n1, 0.95


def k_iron(nb, g, p):
    """Кровельное железо: волна по атрибуту sheet (рельеф и меление краски на гребнях), сверху краска с ржавыми
    пятнами и потёками по впадинам, снизу оцинковка (белая и рыжая ржавчина)."""
    side = p.get('side', 1.0)
    su, sv, _ = sheet_coords(nb)
    wave = nb.mapr(nb.math('COSINE', nb.math('MULTIPLY', su, 2 * math.pi / IRON_PITCH)), -1.0, 1.0, 0.0, 1.0)
    h = wave if side > 0 else nb.math('SUBTRACT', 1.0, wave)
    n_big = nb.noise(g.pos, scale=1.4, detail=5.0)
    if side > 0:
        paint = p.get('paint', (0.24, 0.34, 0.24))
        col = nb.mixc(n_big, nb.scalec(paint, 0.72), nb.scalec(paint, 1.12))
        col = nb.mixc(nb.mapr(wave, 0.6, 1.0, 0.0, 0.1), col, nb.scalec(paint, 1.4))
        loss = nb.mapr(nb.noise(g.pos, scale=2.6, detail=6.0, rough=0.7, distortion=0.4), 0.52, 0.72, 0.0,
                       p.get('rust', 0.85))
    else:
        galv = p.get('galv', (0.42, 0.43, 0.43))
        col = nb.mixc(n_big, nb.scalec(galv, 0.75), nb.scalec(galv, 1.1))
        col = nb.mixc(nb.mapr(nb.noise(g.pos, scale=5.0, detail=4.0), 0.55, 0.7, 0.0, 0.5), col, (0.62, 0.62, 0.60))
        loss = nb.mapr(nb.noise(g.pos, scale=2.2, detail=6.0, rough=0.7, distortion=0.4), 0.48, 0.70, 0.0,
                       p.get('rust', 0.9))
    rust_c = nb.mixc(nb.noise(g.pos, scale=12.0, detail=5.0), (0.38, 0.17, 0.07), (0.20, 0.10, 0.05))
    st = nb.noise(nb.comb(nb.math('MULTIPLY', su, 40.0), nb.math('MULTIPLY', sv, 1.5), 0.0), scale=1.0, detail=3.0)
    streak = nb.math('MULTIPLY', nb.mapr(st, 0.5, 0.7, 0.0, 0.5), nb.mapr(nb.math('SUBTRACT', 1.0, h), 0.5, 1.0, 0.0, 1.0))
    rust = nb.math('MAXIMUM', loss, streak)
    col = nb.mixc(rust, col, rust_c)
    pits = nb.mapr(nb.voronoi(g.pos, scale=60.0), 0.0, 0.08, 1.0, 0.0)
    col = nb.mixc(nb.math('MULTIPLY', nb.math('MULTIPLY', pits, rust), 0.6), col, (0.10, 0.05, 0.03))
    h2 = nb.math('ADD', h, nb.math('MULTIPLY', nb.math('MULTIPLY', rust, nb.noise(g.pos, scale=40.0, detail=4.0)), 0.06))
    return rm._finish_color(nb, g, col, p, soot_amt=0.6), h2, 0.7


def k_wood(nb, g, p):
    """Выветренная древесина (волокна вдоль X): серо-бурая, трещины усушки вдоль волокон, сучки."""
    base = p.get('base', (0.36, 0.30, 0.23))
    grey = p.get('grey', (0.42, 0.40, 0.37))
    gv = nb.comb(nb.math('MULTIPLY', g.x, 1.3), nb.math('MULTIPLY', g.y, 42.0), nb.math('MULTIPLY', g.z, 42.0))
    streak = nb.noise(gv, scale=1.0, detail=6.0, rough=0.62)
    col = nb.mixc(nb.mapr(streak, 0.3, 0.7, 0.0, 1.0), nb.scalec(base, 0.72), nb.scalec(base, 1.12))
    weather = nb.mapr(nb.noise(g.pos, scale=2.2, detail=4.0), 0.35, 0.65, 0.35, 0.9)
    col = nb.mixc(weather, col, nb.scalec(grey, nb.mapr(streak, 0.3, 0.7, 0.85, 1.1)))
    cv = nb.comb(nb.math('MULTIPLY', g.x, 0.7), nb.math('MULTIPLY', g.y, 22.0), nb.math('MULTIPLY', g.z, 22.0))
    crack = nb.mapr(nb.voronoi(cv, scale=1.0, feature='DISTANCE_TO_EDGE'), 0.0, 0.06, 1.0, 0.0)
    crack = nb.math('MULTIPLY', crack, nb.mapr(nb.noise(g.pos, scale=3.0), 0.45, 0.6, 0.0, 0.85))
    col = nb.mixc(crack, col, (0.05, 0.045, 0.04))
    kv = nb.comb(nb.math('MULTIPLY', g.x, 2.5), nb.math('MULTIPLY', g.y, 9.0), nb.math('MULTIPLY', g.z, 9.0))
    kcell = nb.sep(nb.voronoi(kv, scale=1.0, out='Color'))[0]
    knot = nb.math('MULTIPLY', nb.mapr(nb.voronoi(kv, scale=1.0), 0.0, 0.12, 1.0, 0.0), nb.math('GREATER_THAN', kcell, 0.88))
    col = nb.mixc(knot, col, (0.12, 0.08, 0.05))
    h = nb.math('SUBTRACT', nb.math('MULTIPLY', streak, 0.6), nb.math('MULTIPLY', crack, 0.7))
    return rm._finish_color(nb, g, col, p, soot_amt=0.7), h, 0.9


def k_wood_split(nb, g, p):
    """Расщеп, слом и торец: светлее и теплее, волокна вдоль X (на торце — точками), тронутые серостью."""
    base = p.get('base', (0.50, 0.40, 0.28))
    fv = nb.comb(nb.math('MULTIPLY', g.x, 2.0), nb.math('MULTIPLY', g.y, 110.0), nb.math('MULTIPLY', g.z, 110.0))
    fib = nb.noise(fv, scale=1.0, detail=5.0, rough=0.65)
    col = nb.scalec(base, nb.mapr(fib, 0.3, 0.7, 0.72, 1.15))
    weather = nb.mapr(nb.noise(g.pos, scale=3.0, detail=4.0), 0.4, 0.65, 0.15, 0.6)
    col = nb.mixc(weather, col, nb.scalec((0.42, 0.40, 0.37), nb.mapr(fib, 0.3, 0.7, 0.8, 1.1)))
    return rm._finish_color(nb, g, col, p, soot_amt=0.7), fib, 0.92


CUSTOM_KINDS = {'brick_unit': k_brick_unit, 'brick_frac': k_brick_frac, 'slate': k_slate, 'slate_edge': k_slate_edge,
                'iron': k_iron, 'wood': k_wood, 'wood_split': k_wood_split}


def fill_material(mat, alias, cfg, ppm, DNB):
    """Узлы материала псевдонима: вид (цвет, высота, шероховатость) -> пестрота по куску, светлые рёбра (Bevel), пыль
    на гранях вверх -> Principled (рельеф Bump поверх Bevel); Emission с номером — для прохода номера; узел картинки."""
    spec = ALIASES[alias]
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    nb = DNB(nt)
    out = nb.node('ShaderNodeOutputMaterial')
    bsdf = nb.node('ShaderNodeBsdfPrincipled')
    g = rm.Ctx(nb, ppm)
    params = dict(spec.get('params', {}))
    params.setdefault('ao', 0.0)        # AO — своим проходом запечки
    params.setdefault('soot', 0.6)      # копоть — только старыми пятнами
    params.setdefault('stain', 0.3)
    fn = CUSTOM_KINDS.get(spec['kind']) or rm.KINDS[spec['kind']]
    col, height, rough = fn(nb, g, params)
    geo = nb.node('ShaderNodeNewGeometry')
    nz = nb.sep(geo.outputs['Normal'])[2]
    vary = spec.get('vary', 0.08)
    col = nb.scalec(col, nb.mapr(obj_rand(nb), 0.0, 1.0, 1.0 - vary, 1.0 + vary))
    normal_in = None
    bevel_r = spec.get('bevel', cfg['bevel'])
    if bevel_r > 0:
        bev = nb.node('ShaderNodeBevel')
        bev.samples = 8
        bev.inputs['Radius'].default_value = bevel_r
        normal_in = bev.outputs['Normal']
        dot = nb.node('ShaderNodeVectorMath', operation='DOT_PRODUCT')
        nt.links.new(bev.outputs['Normal'], dot.inputs[0])
        nt.links.new(geo.outputs['Normal'], dot.inputs[1])
        edge = nb.mapr(dot.outputs['Value'], 0.985, 0.85, 0.0, 1.0)
        col = nb.mixc(nb.math('MULTIPLY', edge, spec.get('edge', 0.35)), col, nb.scalec(col, spec.get('edge_gain', 1.35)))
    dust = nb.math('MULTIPLY', nb.math('MULTIPLY', nb.mapr(nz, 0.35, 0.9, 0.0, 1.0),
                                       nb.mapr(nb.noise(g.pos, scale=5.0, detail=4.0), 0.35, 0.65, 0.3, 1.0)),
                   spec.get('dust', cfg['dust']))
    col = nb.mixc(dust, col, DUST)
    rough_s = nb.mixf(nb.noise(g.pos, scale=9.0, detail=3.0), rough - 0.06, min(1.0, rough + 0.03))
    rough_s = nb.mixf(dust, rough_s, 1.0)
    nt.links.new(col, bsdf.inputs['Base Color'])
    nt.links.new(rough_s, bsdf.inputs['Roughness'])
    bsdf.inputs['Metallic'].default_value = 0.0
    bump = nb.node('ShaderNodeBump')
    bump.inputs['Strength'].default_value = spec.get('bump_strength', 0.7)
    bump.inputs['Distance'].default_value = spec.get('bump', cfg['bump'])
    nt.links.new(height, bump.inputs['Height'])
    if normal_in is not None:
        nt.links.new(normal_in, bump.inputs['Normal'])
    nt.links.new(bump.outputs['Normal'], bsdf.inputs['Normal'])
    em = nb.node('ShaderNodeEmission')
    em.inputs['Color'].default_value = (ALIAS_IDS[alias] / 64.0, 0.0, 0.0, 1.0)
    em.inputs['Strength'].default_value = 1.0
    tex = nb.node('ShaderNodeTexImage')
    nt.nodes.active = tex
    nt.links.new(bsdf.outputs['BSDF'], out.inputs['Surface'])
    return dict(out=out, bsdf=bsdf, em=em, tex=tex)


def set_surface(info, which):
    out = info['out']
    nt = out.id_data
    for link in list(out.inputs['Surface'].links):
        nt.links.remove(link)
    src = info['em'].outputs['Emission'] if which == 'id' else info['bsdf'].outputs['BSDF']
    nt.links.new(src, out.inputs['Surface'])


# ================================================================== сцена запечки, развёртка, запечка

def setup_scene(threads):
    sc = eg.reset_scene()
    sc.render.engine = 'CYCLES'
    sc.cycles.device = 'CPU'
    sc.cycles.use_denoising = False
    try:
        sc.cycles.use_adaptive_sampling = False
    except AttributeError:
        pass
    sc.render.threads_mode = 'FIXED'
    sc.render.threads = threads
    world = bpy.data.worlds.new('w')
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Color'].default_value = (1, 1, 1, 1)
    sc.world = world
    sc.render.bake.use_selected_to_active = False
    return sc


def piece_object(piece, name, mats, layout):
    m = piece.mesh
    R = piece.offset
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bv = [bm.verts.new(v + R) for v in m.verts]
    bm.verts.ensure_lookup_table()
    uvl = bm.loops.layers.uv.new('UVMap')
    bm.loops.layers.uv.new('UV2')
    slots = []
    for idx, alias, uvs in m.faces:
        try:
            f = bm.faces.new([bv[i] for i in idx])
        except ValueError:
            continue
        if alias not in slots:
            slots.append(alias)
        f.material_index = slots.index(alias)
        if uvs:
            for loop, uv in zip(f.loops, uvs):
                loop[uvl].uv = uv
    bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method='BEAUTY', ngon_method='BEAUTY')
    bm.to_mesh(me)
    bm.free()
    col = me.color_attributes.new(name='Col', type='BYTE_COLOR', domain='CORNER')
    col.data.foreach_set('color', np.tile(np.array(NO_WIND, np.float32), len(col.data)))
    nv = len(me.vertices)
    for key, vals in m.vattr.items():
        a = me.attributes.new(name=key, type='FLOAT_VECTOR', domain='POINT')
        arr = np.zeros((nv, 3), np.float32)
        for i, val in vals.items():
            arr[i] = val
        a.data.foreach_set('vector', arr.ravel())
    s = me.attributes.new(name='soot', type='FLOAT', domain='POINT')
    s.data.foreach_set('value', np.full(nv, piece.soot, np.float32))
    for alias in slots:
        me.materials.append(mats[alias])
    obj = bpy.data.objects.new(name, me)
    eg.link(obj)
    obj.matrix_world = Matrix.Translation(layout) @ piece.pose @ Matrix.Translation(-R)
    if piece.smooth is None:
        me.polygons.foreach_set('use_smooth', [False] * len(me.polygons))
    else:
        me.polygons.foreach_set('use_smooth', [True] * len(me.polygons))
        me.set_sharp_from_angle(angle=math.radians(piece.smooth))
    me.update()
    return obj


def edit_mode(objs):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')


def unwrap(objs, pieces, margin):
    """Своя развёртка (листы — по длине дуги волны) или Smart UV; затем одинаковая плотность и упаковка всех островов
    класса в один атлас."""
    sc = bpy.context.scene
    sc.tool_settings.use_uv_select_sync = True
    auto = [o for o, p in zip(objs, pieces) if p.auto_uv]
    if auto:
        edit_mode(auto)
        bpy.ops.uv.smart_project(angle_limit=math.radians(66.0), island_margin=0.01, area_weight=0.0, correct_aspect=True,
                                 scale_to_bounds=False)
        bpy.ops.object.mode_set(mode='OBJECT')
    edit_mode(objs)
    try:
        bpy.ops.uv.select_all(action='SELECT')
    except RuntimeError:
        pass
    bpy.ops.uv.average_islands_scale()
    bpy.ops.uv.pack_islands(rotate=True, margin_method='FRACTION', margin=margin, shape_method='CONCAVE')
    bpy.ops.object.mode_set(mode='OBJECT')


def texel_density(objs, size):
    a3 = auv = 0.0
    for o in objs:
        me = o.data
        uv = me.uv_layers['UVMap'].data
        for poly in me.polygons:
            vs = [me.vertices[i].co for i in poly.vertices]
            us = [uv[li].uv for li in poly.loop_indices]
            for k in range(1, len(vs) - 1):
                a3 += ((vs[k] - vs[0]).cross(vs[k + 1] - vs[0])).length * 0.5
                auv += abs((us[k].x - us[0].x) * (us[k + 1].y - us[0].y) - (us[k + 1].x - us[0].x) * (us[k].y - us[0].y)) * 0.5
    return size * math.sqrt(auv / max(a3, 1e-9)), a3


def bake(objs, infos, img, btype, samples, margin, **kw):
    for info in infos.values():
        info['tex'].image = img
    sc = bpy.context.scene
    sc.cycles.samples = samples
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    t = time.time()
    bpy.ops.object.bake(type=btype, margin=margin, margin_type='EXTEND', use_clear=True, target='IMAGE_TEXTURES',
                        use_selected_to_active=False, **kw)
    w, h = img.size
    arr = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(arr)
    print('DEBRIS_BAKE', btype, '%.1f s' % (time.time() - t), flush=True)
    return arr.reshape(h, w, 4)[::-1].copy()


def dilate(arr, cov, iters):
    a = arr.copy()
    filled = cov.copy()
    for _ in range(iters):
        acc = np.zeros_like(a)
        cnt = np.zeros(filled.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            sh = np.roll(np.roll(filled, dy, 0), dx, 1)
            acc += np.roll(np.roll(a, dy, 0), dx, 1) * sh[..., None]
            cnt += sh
        grow = (~filled) & (cnt > 0)
        a[grow] = acc[grow] / cnt[grow][:, None]
        filled |= grow
    return a, filled


def pushpull(arr, cov):
    """Заполнить пустое атласа размытым продолжением островов (мипы не тянут чёрное)."""
    levels = []
    c = arr * cov[..., None]
    w = cov.astype(np.float32)
    while c.shape[0] > 1 and c.shape[1] > 1:
        levels.append((c, w))
        h2, w2 = c.shape[0] // 2, c.shape[1] // 2
        c = c.reshape(h2, 2, w2, 2, -1).sum((1, 3))
        w = w.reshape(h2, 2, w2, 2).sum((1, 3))
    fill_v = c / np.maximum(w, 1e-8)[..., None]
    for c, w in reversed(levels):
        up = np.repeat(np.repeat(fill_v, 2, 0), 2, 1)
        fill_v = np.where((w > 0)[..., None], c / np.maximum(w, 1e-8)[..., None], up)
    return np.where(cov[..., None], arr, fill_v)


def fill_outside(arr, cov, iters=6):
    a, filled = dilate(arr, cov, iters)
    return pushpull(a, filled)


def compose(px):
    idm = np.rint(px['id'][..., 0] * 64.0).astype(np.int32)
    cov = idm > 0
    ao = np.clip(px['ao'][..., 0], 0.0, 1.0)
    alb = np.clip(px['color'][..., :3], 0.0, 1.0) * (0.78 + 0.22 * ao)[..., None]
    means, gains = {}, {}
    lw = np.array([0.2126, 0.7152, 0.0722], np.float32)

    def desat(rgb):
        lum = rgb @ lw
        return lum[:, None] + (rgb - lum[:, None]) * SATURATION
    order = sorted(ALIAS_IDS, key=lambda a: 'grade_like' in ALIASES[a])
    for alias in order:
        m = idm == ALIAS_IDS[alias]
        if not m.any():
            continue
        before = alb[m].mean(0)
        ref = ALIASES[alias].get('grade_like')
        if ref in gains:
            alb[m] = np.clip(desat(alb[m]) * gains[ref], 0.0, 1.0)
        else:
            gains[alias] = np.asarray(ALIASES[alias]['target'], np.float32) / np.maximum(desat(alb[m]).mean(0), 1e-4)
            alb[m] = eg.grade_albedo(alb[m], ALIASES[alias]['target'], SATURATION)
        means[alias] = dict(texels=int(m.sum()), before=[round(float(x), 4) for x in before],
                            after=[round(float(x), 4) for x in alb[m].mean(0)])
    smooth = np.clip(1.0 - px['rough'][..., 0], 0.08, 0.30)
    nrm = px['normal'][..., :3]
    mask = np.stack([np.zeros_like(ao), ao, np.full_like(ao, 0.5), smooth], -1)
    alb = fill_outside(alb, cov)
    nrm = fill_outside(nrm, cov)
    mask = fill_outside(mask, cov)
    n = nrm * 2 - 1
    n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6)
    return alb, n * 0.5 + 0.5, mask, cov, means


def material_entry(mat_id):
    return dict(id=mat_id, role='bark', albedo=mat_id + '_albedo.png', normal=mat_id + '_normal.png',
                mask=mat_id + '_mask.png', tiling=[1, 1], normalScale=1.0, smoothnessMax=1.0, aoMax=1.0, stiffness=1.0,
                drag=0.0001, initialBend=0.0, heightDrag=5.0)


def export_asset(asset, items, cfg, out, atlas_dir, ppm, size):
    for o, piece in items:
        o.matrix_world = piece.pose @ Matrix.Translation(-piece.offset)
    bpy.ops.object.select_all(action='DESELECT')
    for o, _ in items:
        o.select_set(True)
    bpy.context.view_layer.objects.active = items[0][0]
    if len(items) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.name = asset.name
    obj.data.name = asset.name
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    me = obj.data
    holder = bpy.data.materials.get(cfg['material']) or bpy.data.materials.new(cfg['material'])
    me.materials.clear()
    me.materials.append(holder)
    me.polygons.foreach_set('material_index', [0] * len(me.polygons))
    for name in ('sheet', 'soot'):
        if name in me.attributes:
            me.attributes.remove(me.attributes[name])
    co = np.zeros(len(me.vertices) * 3, np.float32)
    me.vertices.foreach_get('co', co)
    co = co.reshape(-1, 3)
    zmax = max(float(co[:, 2].max()), 1e-3)
    uv2 = me.uv_layers['UV2'].data
    for poly in me.polygons:
        for li in poly.loop_indices:
            uv2[li].uv = (0.0, max(0.0, me.vertices[me.loops[li].vertex_index].co.z) / zmax)
    me.update()
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    folder = os.path.join(out, asset.name)
    if os.path.isdir(folder):
        shutil.rmtree(folder)
    os.makedirs(folder)
    fbx = asset.name + '.fbx'
    eg.export_fbx([obj], os.path.join(folder, fbx))
    for suffix in ('_albedo.png', '_normal.png', '_mask.png'):
        shutil.copyfile(os.path.join(atlas_dir, cfg['material'] + suffix), os.path.join(folder, cfg['material'] + suffix))
    size3 = [round(float(x), 3) for x in co.max(0) - co.min(0)]
    lo, hi = asset.budget
    manifest = dict(name=asset.name, type='detail', group=GROUP, height=round(float(co[:, 2].max()), 3), trunkRadius=0.0,
                    colliderHeight=0.0, evergreen=False,
                    lods=[dict(fbx=fbx, screen=0.0, submeshes=[cfg['material']], castShadows=cfg['cast'], triangles=tris)],
                    materials=[material_entry(cfg['material'])], nr=dict(cfg['nr']), size=size3, triangles=tris,
                    budget=[lo, hi], texelDensity=round(ppm), atlas=size, pieces=len(items),
                    source='procedural (debris_gen.py); shaders ruinmats (ruins library) + own; baked in Cycles (CPU)')
    with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=1)
    twin = eg.write_prop_twin(folder, manifest) if cfg['prop'] else None
    ok = lo <= tris <= hi
    print('DEBRIS_ASSET', asset.name, json.dumps(dict(triangles=tris, budget=[lo, hi], ok=ok, size=size3,
                                                      pieces=len(items))), flush=True)
    return dict(triangles=tris, budget=[lo, hi], ok=ok, size=size3, pieces=len(items), folder=folder,
                prop=os.path.basename(twin) if twin else None)


def run_class(cls, assets, out, args):
    cfg = CLASSES[cls]
    size = 512 if args.draft else cfg['size']
    t0 = time.time()
    setup_scene(args.threads)
    sc = bpy.context.scene
    sc.world.light_settings.distance = cfg['ao_dist']
    mats = {}
    objs, owners = [], []
    for ai, asset in enumerate(assets):
        layout = Vector((ai * cfg['spacing'], 0.0, 0.0))
        orng = random.Random(zlib.crc32(('noise' + asset.name).encode()))
        for pi, piece in enumerate(asset.pieces):
            piece.mesh.compact()
            if piece.offset is None:
                piece.offset = Vector((orng.uniform(-40, 40), orng.uniform(-40, 40), orng.uniform(-40, 40)))
            for alias in {f[1] for f in piece.mesh.faces}:
                if alias not in mats:
                    mats[alias] = bpy.data.materials.new('dbr_' + alias)
            o = piece_object(piece, '%s_%03d' % (asset.name, pi), mats, layout)
            objs.append(o)
            owners.append((asset, piece))
    # земля для AO (в запечку не входит)
    bpy.ops.mesh.primitive_plane_add(size=max(60.0, len(assets) * cfg['spacing'] * 2 + 10),
                                     location=((len(assets) - 1) * cfg['spacing'] / 2, 0.0, 0.0))
    unwrap(objs, [p for _, p in owners], cfg['margin'])
    ppm, area = texel_density(objs, size)
    print('DEBRIS_CLASS', cls, 'objects', len(objs), 'area %.2f m2' % area, 'texel %.0f px/m' % ppm, flush=True)
    DNB = make_nb_class()
    infos = {alias: fill_material(mat, alias, cfg, ppm, DNB) for alias, mat in mats.items()}
    imgs = {}
    for name in ('id', 'color', 'normal', 'rough', 'ao'):
        img = bpy.data.images.new('%s_%s' % (cls, name), size, size, alpha=True, float_buffer=True)
        img.colorspace_settings.name = 'Non-Color'
        imgs[name] = img
    q = 0.25 if args.draft else 1.0
    px = {}
    for info in infos.values():
        set_surface(info, 'id')
    px['id'] = bake(objs, infos, imgs['id'], 'EMIT', 1, 0)
    for info in infos.values():
        set_surface(info, 'bsdf')
    margin = max(4, size // 128)
    px['color'] = bake(objs, infos, imgs['color'], 'DIFFUSE', max(1, int(4 * q)), margin, pass_filter={'COLOR'})
    px['normal'] = bake(objs, infos, imgs['normal'], 'NORMAL', max(1, int(4 * q)), margin, normal_space='TANGENT',
                        normal_r='POS_X', normal_g='POS_Y', normal_b='POS_Z')
    px['rough'] = bake(objs, infos, imgs['rough'], 'ROUGHNESS', 1, margin)
    px['ao'] = bake(objs, infos, imgs['ao'], 'AO', max(4, int(32 * q)), margin)
    alb, nrm, mask, cov, means = compose(px)
    atlas_dir = os.path.join(out, '_atlas')
    os.makedirs(atlas_dir, exist_ok=True)
    mid = cfg['material']
    eg.save_png(alb, os.path.join(atlas_dir, mid + '_albedo.png'))
    eg.save_png(nrm, os.path.join(atlas_dir, mid + '_normal.png'), linear=True)
    eg.save_png(mask, os.path.join(atlas_dir, mid + '_mask.png'), linear=True)
    results = {}
    for asset in assets:
        items = [(o, p) for o, (a, p) in zip(objs, owners) if a is asset]
        results[asset.name] = export_asset(asset, items, cfg, out, atlas_dir, ppm, size)
    rep = dict(material=mid, atlas=size, texelDensity=round(ppm), targetDensity=cfg['target_ppm'],
               coverage=round(float(cov.mean()), 3), surface_m2=round(area, 2), seconds=round(time.time() - t0, 1),
               aliases=means, assets=results)
    print('DEBRIS_CLASS_DONE', cls, json.dumps(dict(texelDensity=rep['texelDensity'], coverage=rep['coverage'],
                                                    seconds=rep['seconds'])), flush=True)
    return rep


# ================================================================== предпросмотр (Cycles на процессоре)

def preview_material(folder, mat_id):
    m = bpy.data.materials.new('prev_' + mat_id)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes['Principled BSDF']
    alb = nt.nodes.new('ShaderNodeTexImage')
    alb.image = bpy.data.images.load(os.path.join(folder, mat_id + '_albedo.png'), check_existing=True)
    nrm = nt.nodes.new('ShaderNodeTexImage')
    nrm.image = bpy.data.images.load(os.path.join(folder, mat_id + '_normal.png'), check_existing=True)
    nrm.image.colorspace_settings.name = 'Non-Color'
    msk = nt.nodes.new('ShaderNodeTexImage')
    msk.image = bpy.data.images.load(os.path.join(folder, mat_id + '_mask.png'), check_existing=True)
    msk.image.colorspace_settings.name = 'Non-Color'
    msk.image.alpha_mode = 'CHANNEL_PACKED'
    sep = nt.nodes.new('ShaderNodeSeparateColor')
    nt.links.new(msk.outputs['Color'], sep.inputs['Color'])
    mix = nt.nodes.new('ShaderNodeMix')
    mix.data_type = 'RGBA'
    mix.blend_type = 'MULTIPLY'
    mix.inputs[0].default_value = 0.5
    nt.links.new(alb.outputs['Color'], mix.inputs[6])
    nt.links.new(sep.outputs['Green'], mix.inputs[7])
    nt.links.new(mix.outputs[2], bsdf.inputs['Base Color'])
    inv = nt.nodes.new('ShaderNodeMath')
    inv.operation = 'SUBTRACT'
    inv.inputs[0].default_value = 1.0
    nt.links.new(msk.outputs['Alpha'], inv.inputs[1])
    nt.links.new(inv.outputs['Value'], bsdf.inputs['Roughness'])
    nm = nt.nodes.new('ShaderNodeNormalMap')
    nm.uv_map = 'UVMap'
    nt.links.new(nrm.outputs['Color'], nm.inputs['Color'])
    nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    bsdf.inputs['Metallic'].default_value = 0.0
    return m


def render_preview(out, built, args):
    sc = setup_scene(args.threads)
    sc.cycles.samples = 16 if args.draft else 64
    sc.cycles.use_denoising = True
    for attr, val in (('denoiser', 'OPENIMAGEDENOISE'), ('denoising_use_gpu', False)):
        try:
            setattr(sc.cycles, attr, val)
        except (AttributeError, TypeError):
            pass
    try:
        sc.render.compositor_device = 'CPU'
    except (AttributeError, TypeError):
        pass
    sc.render.resolution_x, sc.render.resolution_y = (800, 450) if args.draft else (1600, 900)
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = False
    sc.view_settings.view_transform = 'Standard'
    sc.view_settings.look = 'None'
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGB'
    bpy.ops.mesh.primitive_plane_add(size=40.0, location=(0.0, 0.5, 0.0))
    gm = bpy.data.materials.new('ground')
    gm.use_nodes = True
    gb = gm.node_tree.nodes['Principled BSDF']
    gb.inputs['Base Color'].default_value = (0.17, 0.17, 0.17, 1.0)
    gb.inputs['Roughness'].default_value = 0.95
    bpy.context.active_object.data.materials.append(gm)
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = 3.2
    sun.angle = math.radians(2.0)
    so = bpy.data.objects.new('sun', sun)
    eg.link(so)
    so.rotation_euler = (math.radians(48), 0.0, math.radians(152))
    sc.world.node_tree.nodes['Background'].inputs['Color'].default_value = (0.55, 0.62, 0.72, 1.0)
    sc.world.node_tree.nodes['Background'].inputs['Strength'].default_value = 0.9
    label_mat = bpy.data.materials.new('label')
    label_mat.use_nodes = True
    label_mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.75, 0.75, 0.72, 1.0)
    rows = [('small', -2.2, 1.55), ('medium', 0.25, 2.45), ('large', 3.05, 3.35)]
    for cls, y, dx in rows:
        names = [a.name for a in built.get(cls, [])]
        for k, name in enumerate(names):
            folder = os.path.join(out, name)
            with open(os.path.join(folder, 'manifest.json'), encoding='utf-8') as f:
                manifest = json.load(f)
            before = set(bpy.data.objects)
            bpy.ops.import_scene.fbx(filepath=os.path.join(folder, manifest['lods'][0]['fbx']))
            new = [o for o in bpy.data.objects if o not in before and o.type == 'MESH']
            obj = new[0]
            bpy.ops.object.select_all(action='DESELECT')
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj
            bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
            obj.data.materials.clear()
            obj.data.materials.append(preview_material(folder, manifest['materials'][0]['id']))
            x = (k - (len(names) - 1) / 2) * dx
            obj.location = (x, y, 0.0)
            ext = manifest['size'][1]
            cu = bpy.data.curves.new('label_' + name, 'FONT')
            cu.body = name.replace('debris_', '')
            cu.size = 0.13
            cu.align_x = 'CENTER'
            to = bpy.data.objects.new('label_' + name, cu)
            eg.link(to)
            to.location = (x, y - max(0.42, ext * 0.5 + 0.22), 0.002)
            cu.materials.append(label_mat)
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    eg.link(cam)
    cam.data.lens = 30.0
    cam.location = (0.0, -5.22, 7.08)
    target = Vector((0.0, -0.26, 0.0))
    cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
    sc.camera = cam
    path = os.path.join(out, 'preview.png')
    sc.render.filepath = path
    t = time.time()
    bpy.ops.render.render(write_still=True)
    print('DEBRIS_PREVIEW', path, '%.1f s' % (time.time() - t), flush=True)
    return path


# ================================================================== запуск

def main():
    global rm
    ap = argparse.ArgumentParser()
    ap.add_argument('out', nargs='?', default=OUT_DEFAULT)
    ap.add_argument('--ruinmats', default=RUINMATS)
    ap.add_argument('--draft', action='store_true')
    ap.add_argument('--assets', nargs='*')
    ap.add_argument('--no-preview', action='store_true')
    ap.add_argument('--preview-only', action='store_true')
    ap.add_argument('--threads', type=int, default=max(2, (os.cpu_count() or 4) - 2))
    args = ap.parse_args(eg.script_args())
    if not os.path.exists(os.path.join(args.ruinmats, 'ruinmats.py')):
        raise SystemExit('ruinmats.py not found in ' + args.ruinmats + ' (pass --ruinmats)')
    sys.path.insert(0, args.ruinmats)
    import ruinmats  # noqa: E402
    rm = ruinmats
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    built = {}
    t0 = time.time()
    for name, cls, budget, fn in ASSETS:
        if args.assets and name not in args.assets:
            continue
        rng = random.Random(zlib.crc32(name.encode()))
        asset = Asset(name, cls, budget)
        fn(asset, rng)
        recentre(asset)
        built.setdefault(cls, []).append(asset)
        print('DEBRIS_BUILT', name, asset.tris(), budget, len(asset.pieces), flush=True)
    report = dict(classes={})
    if not args.preview_only:
        for cls in ('small', 'medium', 'large'):
            if cls in built:
                report['classes'][cls] = run_class(cls, built[cls], out, args)
        report['seconds'] = round(time.time() - t0, 1)
        with open(os.path.join(out, 'debris_report.json'), 'w', encoding='utf-8') as f:
            json.dump(report, f, indent=1)
    if not args.no_preview:
        render_preview(out, built, args)
    print('DEBRIS_OK', '%.1f s' % (time.time() - t0), flush=True)


main()
