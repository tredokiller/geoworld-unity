# Сосна обыкновенная (Pinus sylvestris) боров на песчаных террасах Северского Донца (Змиёв, Чугуев): 4 спелых лесных
# дерева (20-26 м: длинный очищенный ствол с зоной обломков сучьев, живая крона в верхних 30-40 %, неровная
# уплощённая/округлая лопастная вершина) и 2 молодых/опушечных (9.5-12.5 м, крона до 1/3-1/5 высоты, гуще). Для Unity:
# VegetationImporter (manifest.json + FBX LOD0..LOD2 + крест + PNG).
#
# Запуск:  blender.exe -b --factory-startup --python pine_gen.py -- <папка вывода> [имя ...] [--textures]
#   --textures — заново сделать кору и атласы хвои (иначе берутся из <папка вывода>/_tex).
#
# Скелет: ствол по таблице сбега с корневыми лапами, поверхностными корнями на песке и продольным рельефом коры;
# мутовки по годичному приросту (кривая Чепмена-Ричардса); живых ветвей в мутовке у вершины 4-5, в середине 2-3,
# внизу 1-2; угол от ствола 38° + 50°·d^0.8; ветвь: тангаж φ0(1-s)^1.5 + φкончика s^1.5 - провис·sin(πs), кончик всегда
# вверх, блуждание и изломы (у старого дерева — извилистые толстые сучья). Хвоя — «подушки»: внешние 1.3-1.9 м каждой
# ветви, боковые веточки на узлах через 13-18 см почти в плоскости ветви, на каждой — веер (карточка атласа со сгибом
# по оси), часть веточек и щёток торчит вверх, у части вееров вертикальная карточка-профиль; внутри — голые ветви и
# сухие серые веточки. Ниже кроны — сухие провисшие ветви с веточками, ниже — обломки сучьев (серое дерево: UV в
# серую пластину нижней коры). Кора: нижняя пластинчатая до rough_top с языками вверх по бороздам — граница проходит
# по вершинам сетки ствола (рваная ломаная, без ступенек граней), грубая кора толще («ступенька» в месте перехода);
# выше — «лисья» (ствол, толстые ветви); тонкие побеги и ветви молодых деревьев — серо-бурые (нижняя кора мелко).
# LOD1/LOD2 — из того же скелета: вееры одной ветви группируются, группа -> карточка с той же площадью хвои (подогнана
# по силуэту LOD0), тем же центром и ориентацией. Крест: 2 вертикальные плоскости (4 вида, сдвинуты так, что
# пересекаются по оси ствола и центру кроны — без «второго ствола») + горизонтальная шапка с видом сверху; цвет
# креста подогнан под LOD2 рендером.
import json
import math
import os
import random
import shutil
import sys

import bpy
import numpy as np
from mathutils import Quaternion, Vector, noise

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

UP = Vector((0.0, 0.0, 1.0))
M_LOWER, M_UPPER, M_LEAVES = 0, 1, 2
BARK_UPPER_TILE = 0.5            # м, квадрат
LOD_SCREEN = (0.45, 0.18, 0.06, 0.004)
WIND = dict(stiffness=1.5, drag=0.1)   # одинаково у коры, хвои и креста: подушки не съезжают с ветвей
CROSS_CELL = (384, 512)          # вид сбоку в атласе креста; вид сверху — 512 x 512 справа
CARD_SPRITES = ('spray_large', 'spray_b', 'spray_small', 'spray_side', 'pad')

PRESETS = {
    # спелые лесные: H, DBH, возраст (мутовки), доля высоты до живой кроны, верх сплошной грубой коры (м),
    # радиус кроны R (м, внешний край подушек), вершина, ...
    'pine_forest_01': dict(kind='forest', H=23.0, dbh=0.32, age=75, crown_base=0.63, rough_top=5.0, seed=1101,
                           lean=1.0, top='round', crown_shift=0.6, R=2.8, lobes=5),
    'pine_forest_02': dict(kind='forest', H=25.5, dbh=0.36, age=85, crown_base=0.62, rough_top=6.5, seed=2202,
                           lean=0.8, top='flat', fork=6.5, crown_shift=0.6, R=3.0, lobes=6),
    'pine_forest_03': dict(kind='forest', H=20.0, dbh=0.27, age=60, crown_base=0.60, rough_top=3.8, seed=3303,
                           lean=4.0, top='round', one_sided=0.55, crown_shift=1.3, R=2.6, lobes=4),
    'pine_forest_04': dict(kind='forest', H=23.5, dbh=0.42, age=115, crown_base=0.62, rough_top=10.0, seed=4404,
                           lean=1.5, top='parasol', crown_shift=0.9, limb_scale=1.9, R=3.7, lobes=5, sinuous=True,
                           gap_scale=1.6, max_prim=36),
    'pine_young_01': dict(kind='young', H=9.5, dbh=0.15, age=22, crown_base=0.30, rough_top=1.2, seed=5505,
                          lean=1.0, top='cone', crown_shift=0.3, R=2.0, lobes=5),
    'pine_young_02': dict(kind='young', H=12.5, dbh=0.20, age=30, crown_base=0.17, crown_base_shade=0.50,
                          rough_top=1.8, seed=6606, lean=2.5, top='cone', edge=True, crown_shift=1.5, R=2.6, lobes=4),
}

LOD_CFG = [
    dict(n_base=14, n_band=36, n_up=10, n_crown=7, ring_low=1.0, ring_up=1.6, band_step=0.28,
         prim_sides=(5, 4), prim_segs=6, lat_sides=3, lat_min=0.45, lat_max_n=4, dead_sides=4, stubs=99, roots=5,
         twigs=True),
    dict(n_base=8, n_band=12, n_up=7, n_crown=5, ring_low=2.2, ring_up=3.0, band_step=0.7,
         prim_sides=(4, 3), prim_segs=4, lat_sides=3, lat_min=0.7, lat_max_n=2, dead_sides=3, stubs=10, roots=4,
         twigs=False),
    dict(n_base=6, n_band=6, n_up=5, n_crown=4, ring_low=4.0, ring_up=6.0, band_step=99.0,
         prim_sides=(3, 3), prim_segs=2, lat_sides=0, lat_min=99.0, lat_max_n=0, dead_sides=0, stubs=0, roots=0,
         twigs=False),
]


def lerp(a, b, t):
    return a + (b - a) * t


def clamp(x, a, b):
    return max(a, min(b, x))


def table(x, pts):
    if x <= pts[0][0]:
        return pts[0][1]
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        if x <= x1:
            return lerp(y0, y1, (x - x0) / max(x1 - x0, 1e-9))
    return pts[-1][1]


def rot(v, axis, deg):
    v = v.copy()
    v.rotate(Quaternion(axis, math.radians(deg)))
    return v


def smooth01(t):
    t = clamp(t, 0.0, 1.0)
    return t * t * (3 - 2 * t)


# ------------------------------------------------------------------ скелет

class Branch:
    def __init__(self, pts, radii, wind, level, alive=True, kind='prim'):
        self.pts = [Vector(p) for p in pts]
        self.radii = list(radii)
        self.wind = list(wind)
        self.level = level
        self.alive = alive
        self.kind = kind          # prim | lat | dead | twig | stub | leader | root
        self.children = []
        self.sprays = []
        self.cap = None           # 'broken' — рваный торец
        self.s_pad = 1.0
        self.length = sum((self.pts[i + 1] - self.pts[i]).length for i in range(len(self.pts) - 1))

    def sample(self, s):
        """Точка, касательная, радиус, ветер на доле длины s."""
        target = s * self.length
        acc = 0.0
        for i in range(len(self.pts) - 1):
            seg = (self.pts[i + 1] - self.pts[i]).length
            if acc + seg >= target or i == len(self.pts) - 2:
                f = 0.0 if seg < 1e-9 else min(1.0, max(0.0, (target - acc) / seg))
                return (self.pts[i].lerp(self.pts[i + 1], f), (self.pts[i + 1] - self.pts[i]).normalized(),
                        lerp(self.radii[i], self.radii[i + 1], f), lerp(self.wind[i], self.wind[i + 1], f))
            acc += seg
        return self.pts[-1], (self.pts[-1] - self.pts[-2]).normalized(), self.radii[-1], self.wind[-1]


class Tree:
    """Скелет одного дерева: ствол (функции центра/радиуса/границы коры), лидеры (у развилки), ветви с подушками."""

    def __init__(self, name, P, atlas, bark_info):
        self.name = name
        self.P = P
        self.atlas = atlas
        self.bark = bark_info
        self.rng = random.Random(P['seed'])
        rng = self.rng
        self.H = P['H']
        self.dbh = P['dbh']
        self.young = P['kind'] == 'young'
        self.zcb = P['crown_base'] * self.H
        self.lean_az = rng.uniform(0, math.tau)
        self.lean = math.tan(math.radians(P['lean']))
        self.noise_off = Vector((rng.uniform(0, 100), rng.uniform(0, 100), rng.uniform(0, 100)))
        self.flare_lobes = rng.choice((4, 5))
        self.flare_phase = rng.uniform(0, math.tau)
        self.open_az = self.lean_az + rng.uniform(-0.6, 0.6)    # просвет: туда наклон и смещение кроны
        self.lobe_n = P.get('lobes', 5)
        self.lobe_phase = rng.uniform(0, math.tau)
        self.bark_t = 0.004 if self.young else 0.012            # грубая кора толще лисьей
        self.low_tile = (0.5 * (0.5 if self.young else 1.0), 1.0 * (0.5 if self.young else 1.0))
        self.fork_z = self.H - P['fork'] if P.get('fork') else None
        self.top_z = self.fork_z or self.H
        self.leaders = []
        self.branches = []
        self.stubs = []
        self.roots = []
        self.R = P['R']
        self.make_tongues()
        self.build()

    # ---------------- ствол
    def center(self, z):
        z = max(z, -0.5)
        d = Vector((math.cos(self.lean_az), math.sin(self.lean_az), 0.0)) * (self.lean * max(z, 0.0))
        n = noise.noise_vector(self.noise_off + Vector((0, 0, z / 4.5)))
        return Vector((d.x + n.x * 0.045, d.y + n.y * 0.045, z))

    def radius(self, z, theta=None):
        """Внешний радиус (по грубой коре) с корневыми лапами у комля и продольным рельефом коры."""
        H, r13 = self.H, self.dbh * 0.5
        if self.young:
            pts = [(-0.5, 1.15), (0.0, 1.15), (0.3, 1.07), (1.3, 1.0), (0.25 * H, 0.86), (0.5 * H, 0.62),
                   (0.75 * H, 0.36), (0.95 * H, 0.08), (H, 0.02)]
        else:
            pts = [(-0.5, 1.2), (0.0, 1.2), (0.3, 1.1), (1.3, 1.0), (0.25 * H, 0.9), (0.5 * H, 0.72),
                   (0.65 * H, 0.58), (0.8 * H, 0.38), (0.95 * H, 0.10), (H, 0.02)]
        r = r13 * table(z, pts)
        if theta is not None:
            if z < 1.2:
                lobe = max(0.0, math.cos(self.flare_lobes * (theta - self.flare_phase) * 0.5) ** 2)
                fl = 0.34 * math.exp(-max(z, 0.0) / 0.35)
                r *= 1.0 + fl * (0.4 + 0.6 * lobe)
            amp = 0.035 if z < self.P['rough_top'] + 1.0 else 0.008
            if self.young:
                amp *= 0.5
            ridge = noise.noise(self.noise_off * 0.7 + Vector((math.cos(theta) * 2.6, math.sin(theta) * 2.6, z / 1.1)))
            r *= 1.0 + amp * ridge
        return r

    def make_tongues(self):
        """Языки грубой коры вверх по бороздам: (азимут, полуширина в метрах, высота)."""
        rng = random.Random(self.P['seed'] * 7 + 3)
        self.tongues = []
        n = rng.randint(5, 8) if not self.young else rng.randint(3, 5)
        for _ in range(n):
            h = rng.uniform(0.25, 0.7) if rng.random() < 0.65 else rng.uniform(0.8, 1.4)
            if self.young:
                h *= 0.4
            # язык — полоса шириной в 1-2 пластины (8-18 см) с округлым верхом
            self.tongues.append((rng.uniform(0, math.tau), rng.uniform(0.04, 0.09), h))

    def rough_line(self, theta):
        """Плавная нижняя граница лисьей коры: с одной стороны выше, волнистая."""
        base = self.P['rough_top'] * (1.0 + 0.10 * math.cos(theta - self.open_az + math.pi))
        n = noise.noise(self.noise_off * 0.37 + Vector((math.cos(theta) * 1.4, math.sin(theta) * 1.4, 3.0)))
        return base + (0.3 if not self.young else 0.12) * n

    def tongue_h(self, theta):
        r = self.radius(self.P['rough_top']) + 1e-3
        out = 0.0
        for az, hw, h in self.tongues:
            d = math.atan2(math.sin(theta - az), math.cos(theta - az)) * r
            x = abs(d) / hw
            if x < 1.0:
                out = max(out, h * (1.0 - x ** 4) ** 0.5)
        return out

    def boundary(self, theta, dtheta):
        """Граница коры для колонки шириной dtheta: средняя по колонке высота языков (на грубых LOD языки
        размазываются по площади, средняя линия та же)."""
        k = 9
        t = sum(self.tongue_h(theta + dtheta * ((i + 0.5) / k - 0.5)) for i in range(k)) / k
        return self.rough_line(theta) + t

    def trunk_wind(self, z):
        return 0.2 * max(0.0, (z - self.zcb) / max(self.H - self.zcb, 1.0)) ** 2

    def crown_base_at(self, az):
        """Низ живой кроны по азимуту: у опушечного дерева со стороны поля ниже."""
        P = self.P
        if 'crown_base_shade' in P:
            c = 0.5 * (1 - math.cos(az - self.open_az))   # 0 — сторона поля, 1 — сторона леса
            return lerp(P['crown_base'], P['crown_base_shade'], c ** 0.8) * self.H
        return self.zcb + 0.35 * math.cos(az - self.open_az + math.pi)

    # ---------------- мутовки
    def whorls(self):
        P, rng = self.P, self.rng
        T = P['age']
        k = 0.025
        f = lambda t: (1 - math.exp(-k * t)) ** 1.4
        Hmax = self.H / f(T)
        zs, z = [], 0.0
        for t in range(1, T + 1):
            z += (Hmax * (f(t) - f(t - 1))) * rng.uniform(0.75, 1.25)
            zs.append(z)
        scale = self.H / zs[-1]
        return [z * scale for z in zs]

    def build(self):
        P, rng, H = self.P, self.rng, self.H
        zs = self.whorls()
        young = self.young
        zcb_min = min(self.crown_base_at(a / 24 * math.tau) for a in range(24))
        crown = [z for z in zs if zcb_min <= z < H - 0.15]
        sel = []
        for z in sorted(crown, reverse=True):
            depth = (H - z) / (H - self.zcb)
            gap = ((0.18 + 0.42 * depth) * P.get('gap_scale', 1.0)) if not young else (0.36 + 0.2 * depth)
            if not sel or sel[-1] - z >= gap:
                sel.append(z)
        crown = sorted(sel)
        if self.fork_z:
            self.make_leaders()
        az0 = rng.uniform(0, 360)
        for z in crown:
            depth = clamp((H - z) / (H - self.zcb), 0.0, 1.0)
            if young:
                n = rng.choice((3, 4, 4, 5, 5)) if depth < 0.8 else rng.choice((2, 3, 3, 4))
            else:
                n = max(1, int(round(lerp(4.5, 1.6, depth ** 0.8) + rng.uniform(-0.6, 0.6))))
            az0 += 137.5 + rng.uniform(-30, 30)
            owners = [None]
            if self.fork_z and z > self.fork_z - 0.2:
                owners = self.leaders
                n = max(2, n + 1)
            for b in range(n):
                az = math.radians(az0 + 360.0 * b / n + rng.uniform(-25, 25))
                owner = owners[0] if len(owners) == 1 else min(
                    owners, key=lambda ld: -math.cos(az - ld.az))
                zcb_az = self.crown_base_at(az)
                if z < zcb_az:
                    if z > zcb_az - 2.5 and rng.random() < 0.7:
                        self.make_dead_branch(z, az, young)
                    continue
                if P.get('one_sided') and math.cos(az - self.open_az) < -0.25 and rng.random() < P['one_sided']:
                    continue
                d_local = depth
                if owner is not None:
                    d_local = clamp((owner.top - z) / (owner.top - self.zcb), 0.0, 1.0)
                self.make_primary(z, az, d_local, owner)
        self.limit_primaries(P.get('max_prim', 44 if not young else 46))
        # сухие ветви под кроной и обломки сучьев ниже
        dead_len = rng.uniform(1.6, 2.6) if not young else rng.uniform(0.8, 1.6)
        for z in zs:
            zc = self.crown_base_at(rng.uniform(0, math.tau))
            if zc - dead_len <= z < zc and z < zcb_min:
                for _ in range(rng.choice((1, 2, 2, 3))):
                    self.make_dead_branch(z, rng.uniform(0, math.tau), young)
            elif not young and 0.40 * H <= z < zc - dead_len:
                for _ in range(rng.choice((0, 1, 1, 2, 3))):
                    self.make_stub(z, rng.uniform(0, math.tau), rng.uniform(0.05, 0.4))
            elif young and 0.4 <= z < zc - dead_len:
                for _ in range(rng.choice((1, 2, 3))):
                    if rng.random() < 0.55:
                        self.make_dead_branch(z, rng.uniform(0, math.tau), young, small=True)
                    else:
                        self.make_stub(z, rng.uniform(0, math.tau), rng.uniform(0.05, 0.25))
        if not young:
            for _ in range(rng.randint(4, 7)):   # заросшие сучки на чистом стволе
                self.make_stub(rng.uniform(2.0, 0.40 * H), rng.uniform(0, math.tau), rng.uniform(0.03, 0.08),
                               r_scale=0.7)
        self.make_roots()

    def limit_primaries(self, max_n):
        """Не больше max_n живых скелетных ветвей: лишние — самые короткие в нижней половине кроны (в лесу им и так
        не хватает света); на их место — сухие ветви."""
        live = [b for b in self.branches if b.alive]
        if len(live) <= max_n:
            return
        rng = random.Random(self.P['seed'] + 77)
        by_z = {}
        for b in live:
            by_z.setdefault(round(b.z, 3), []).append(b)
        # прореживание равномерно по всей кроне (кроме трёх верхних мутовок), в каждой мутовке остаётся хотя бы одна
        # ветвь; чаще убираются короткие
        cand = []
        for z, lst in by_z.items():
            if lst[0].depth < 0.12:
                continue
            lst = sorted(lst, key=lambda b: b.length)
            cand += lst[:-1]
        rng.shuffle(cand)
        cand.sort(key=lambda b: b.length * rng.uniform(0.6, 1.4))
        drop = set(id(b) for b in cand[:len(live) - max_n])
        self.branches = [b for b in self.branches if id(b) not in drop]

    def make_leaders(self):
        rng = self.rng
        z0 = self.fork_z
        base = self.center(z0 - 0.3)
        r0 = self.radius(z0) - self.bark_t
        az = rng.uniform(0, math.tau)
        for k, (tilt, top) in enumerate(((rng.uniform(15, 22), self.H), (rng.uniform(18, 26), self.H - rng.uniform(0.5, 1.2)))):
            a = az + k * math.pi + rng.uniform(-0.35, 0.35)
            d = Vector((math.sin(math.radians(tilt)) * math.cos(a), math.sin(math.radians(tilt)) * math.sin(a),
                        math.cos(math.radians(tilt))))
            pts, radii, wind = [], [], []
            n = 7
            L = (top - z0 + 0.3) / d.z * 0.97
            p = base.copy()
            for i in range(n + 1):
                s = i / n
                pts.append(p.copy())
                radii.append(max(0.006, r0 * 0.74 * (1 - s) ** 0.85 + 0.005))
                wind.append(self.trunk_wind(p.z) + 0.05 * s)
                dd = (d + UP * (0.45 * s)).normalized()
                p = p + dd * (L / n)
            ld = Branch(pts, radii, wind, 0, True, 'leader')
            ld.az = a
            ld.top = pts[-1].z
            self.leaders.append(ld)

    def leader_point(self, ld, z):
        for i in range(len(ld.pts) - 1):
            if ld.pts[i + 1].z >= z:
                f = (z - ld.pts[i].z) / max(ld.pts[i + 1].z - ld.pts[i].z, 1e-6)
                return ld.pts[i].lerp(ld.pts[i + 1], f), lerp(ld.radii[i], ld.radii[i + 1], f)
        return ld.pts[-1], ld.radii[-1]

    def make_primary(self, z, az, depth, owner=None):
        P, rng, H = self.P, self.rng, self.H
        young = self.young
        top = P.get('top')
        Lmax = (self.R - 0.25) / 0.8
        if young:
            prof = [(0, 0.22), (0.15, 0.48), (0.35, 0.72), (0.6, 0.9), (0.85, 1.0), (1.0, 0.92)]
        elif top == 'flat':
            prof = [(0, 0.42), (0.08, 0.66), (0.2, 0.88), (0.4, 1.0), (0.6, 0.97), (0.8, 0.86), (1.0, 0.7)]
        elif top == 'parasol':
            # старое дерево: зонтик — шире всего наверху, вершинные сучья догнали лидера
            prof = [(0, 0.85), (0.1, 1.0), (0.3, 1.0), (0.55, 0.85), (0.8, 0.7), (1.0, 0.6)]
        else:
            prof = [(0, 0.2), (0.1, 0.46), (0.25, 0.78), (0.4, 0.97), (0.55, 1.0), (0.75, 0.9), (1.0, 0.72)]
        if owner is not None:   # у развилки — две своих вершинки, ветви от лидера наружу
            prof = [(0, 0.3), (0.15, 0.6), (0.35, 0.85), (0.6, 1.0), (1.0, 0.9)]
        L = Lmax * table(depth, prof)
        c = math.cos(az - self.open_az)
        L *= 1.0 + 0.12 * P.get('crown_shift', 0.5) * c
        if P.get('one_sided'):
            L *= 1.0 + 0.22 * c
        if P.get('edge'):
            L *= 1.0 + 0.28 * c
        L *= 1.0 + 0.18 * math.cos(self.lobe_n * (az - self.lobe_phase))   # лопасти кроны
        L *= rng.uniform(0.72, 1.25)
        L = max(L, 0.35)
        if young:
            theta = 46 + 42 * depth + rng.gauss(0, 7)
        else:
            theta = 38 + 50 * depth ** 0.8 + rng.gauss(0, 8)
        parasol = top == 'parasol' and depth < 0.5
        phi0 = 90 - theta
        phitip = rng.uniform(25, 50) if not young else rng.uniform(32, 55)
        sag = (3 + 20 * min(1.0, (L / 3.0) ** 2)) * rng.uniform(0.7, 1.2)
        if parasol:
            phi0 = rng.uniform(35, 50)
            phitip = rng.uniform(5, 15)
            sag = rng.uniform(0, 5)
        if owner is not None:
            tpt, tr = self.leader_point(owner, z)
        else:
            tpt, tr = self.center(z), self.radius(min(z, self.top_z)) - self.bark_t
        hd = Vector((math.cos(az), math.sin(az), 0.0))
        p = tpt + hd * (tr * 0.2)
        n = 8
        pts = [p.copy()]
        nk = rng.choice((0, 0, 1, 1, 2)) if not P.get('sinuous') else rng.choice((1, 2, 2, 3))
        kinks = sorted(rng.uniform(0.25, 0.8) for _ in range(nk))
        kd = [(rng.uniform(-28, 28), rng.uniform(-16, 16)) for _ in kinks]
        az_w, ph_w = 0.0, 0.0
        wander = 6.0 if not P.get('sinuous') else 8.0
        for i in range(1, n + 1):
            s = (i - 0.5) / n
            if parasol:
                phi = phi0 * (1 - s) ** 2.5 + phitip * (1 - (1 - s) ** 2.5) - sag * math.sin(math.pi * s)
            else:
                phi = phi0 * (1 - s) ** 1.5 + phitip * s ** 1.5 - sag * math.sin(math.pi * s)
            az_w += rng.gauss(0, wander * 0.6)
            ph_w += rng.gauss(0, wander * 0.4)
            ka = sum(d[0] for k, d in zip(kinks, kd) if s > k)
            kp = sum(d[1] for k, d in zip(kinks, kd) if s > k)
            a = az + math.radians(az_w + ka)
            ph = math.radians(clamp(phi + ph_w + kp, -40, 85))
            d = Vector((math.cos(ph) * math.cos(a), math.cos(ph) * math.sin(a), math.sin(ph)))
            p = p + d * (L / n)
            pts.append(p.copy())
        r0 = min((1.0 + 1.3 * L) / 200.0 * P.get('limb_scale', 1.0) * 1.15, 0.38 * tr)
        radii = [max(0.004, r0 * (1 - 0.8 * (i / n)) ** 0.9) for i in range(n + 1)]
        w0 = self.trunk_wind(z)
        wind = [min(1.0, w0 + 0.35 * (i / n) ** 1.5) for i in range(n + 1)]
        br = Branch(pts, radii, wind, 1, True, 'prim')
        br.depth = depth
        br.z = z
        br.az = az
        self.branches.append(br)
        self.make_pad(br)

    def make_pad(self, br):
        """Подушка хвои на внешних pad_len метрах ветви: боковые веточки с веерами, верхушечный веер, щётки вверх;
        внутри — сухие серые веточки."""
        rng, young = self.rng, self.young
        L = br.length
        if young:
            pad_len = L if br.depth < 0.3 else rng.uniform(1.0, 1.5)
        else:
            pad_len = rng.uniform(1.3, 1.9) * self.P.get('limb_scale', 1.0) ** 0.3
        pad_len = min(L, pad_len)
        br.s_pad = 1.0 - pad_len / L
        node = rng.uniform(0.12, 0.16) if not young else rng.uniform(0.2, 0.26)
        lat_max = 1.0 if not young else 1.1
        s = max(br.s_pad, 0.08)
        flip = rng.choice((-1, 1))
        while s < 0.93:
            p, d, r, w = br.sample(s)
            side = d.cross(UP)
            side = side.normalized() if side.length > 1e-6 else Vector((1, 0, 0))
            for sd in (flip, -flip):
                if rng.random() < 0.08:
                    continue
                a = math.radians(rng.uniform(45, 65))
                upper = rng.random() < 0.15
                rise = rng.uniform(0.6, 1.0) if upper else rng.uniform(0.05, 0.3)
                ld = (d * math.cos(a) + side * (sd * math.sin(a)) + UP * rise).normalized()
                ll = min(lat_max, 0.6 * L * (1 - s) + 0.15) * rng.uniform(0.85, 1.1)
                if upper:
                    ll *= 0.6
                if ll < 0.18:
                    continue
                pts = [p.copy()]
                q, dd = p.copy(), ld.copy()
                for _ in range(2):
                    dd = (dd + UP * 0.1 + Vector((rng.gauss(0, 0.05), rng.gauss(0, 0.05), 0))).normalized()
                    q = q + dd * (ll / 2)
                    pts.append(q.copy())
                rr = max(0.003, min(r * 0.6, 0.004 + 0.008 * ll))
                lat = Branch(pts, [rr, rr * 0.7, max(0.0025, rr * 0.4)], [w, w + 0.06, w + 0.12], 2, True, 'lat')
                br.children.append(lat)
                self.lateral_sprays(lat, ll)
            if rng.random() < (0.3 if not young else 0.35):   # щётка вверх с верхней стороны ветви
                ud = (d * 0.45 + UP * 0.9 + side * rng.uniform(-0.3, 0.3)).normalized()
                self.add_spray(br, p, ud, 'spray_small', rng.uniform(0.9, 1.2), w, upright=True)
            flip = -flip
            s += node / L * rng.uniform(0.8, 1.2)
        # верхушечный веер («лапа» на конце ветви)
        reach = self.sprite_reach('spray_large')
        sc = rng.uniform(1.05, 1.3)
        s_t = max(br.s_pad, 1.0 - 0.45 * reach * sc / L)
        p, d, r, w = br.sample(s_t)
        self.add_spray(br, p, (d + UP * 0.15).normalized(), 'spray_large', sc, w)
        # внутри кроны — сухие серые веточки
        s = 0.12
        while s < br.s_pad - 0.03:
            if rng.random() < 0.28:
                p, d, r, w = br.sample(s)
                side = d.cross(UP).normalized() * rng.choice((-1, 1))
                ld = (d * 0.5 + side * 0.85 + UP * rng.uniform(-0.35, 0.1)).normalized()
                ll = rng.uniform(0.15, 0.45)
                tw = Branch([p, p + ld * ll], [max(0.003, r * 0.4), 0.002], [w, w + 0.05], 2, False, 'twig')
                tw.cap = 'none'
                br.children.append(tw)
            s += 0.3 / L

    def sprite_reach(self, sprite):
        spr = self.atlas[sprite]
        return (1.0 - spr['anchor'][1]) * spr['size'][1]

    def lateral_sprays(self, lat, ll):
        rng = self.rng
        p, d, r, w = lat.sample(0.0)
        sprite = ('spray_b' if rng.random() < 0.35 else 'spray_large') if ll >= 0.42 else 'spray_small'
        reach = self.sprite_reach(sprite)
        sc = clamp((ll + 0.08) / reach, 0.78, 1.35)
        self.add_spray(lat, p, d, sprite, sc, w, side_card=sprite != 'spray_small' and rng.random() < 0.22)
        covered = reach * sc
        if ll - covered > 0.22:   # длинная веточка: второй веер на её внешней части
            s2 = clamp(covered * 0.7 / ll, 0.3, 0.8)
            p2, d2, r2, w2 = lat.sample(s2)
            rest = ll * (1 - s2) + 0.08
            sp2 = 'spray_large' if rest >= 0.42 else 'spray_small'
            self.add_spray(lat, p2, d2, sp2, clamp(rest / self.sprite_reach(sp2), 0.78, 1.3), w2)

    def add_spray(self, owner, p, d, sprite, scale, w, side_card=False, upright=False):
        rng = self.rng
        side = d.cross(UP)
        side = side.normalized() if side.length > 1e-6 else Vector((1, 0, 0))
        d2 = d if upright else rot(d, side, rng.uniform(5, 18))    # кончик веера вверх
        n = side.cross(d2).normalized()
        if n.z < 0:
            n = -n
        n = rot(n, d2, rng.uniform(-28, 28))                        # крен вокруг оси
        owner.sprays.append(dict(p=p.copy(), d=d2.normalized(), n=n, sprite=sprite, scale=scale,
                                 w=min(0.8, w + 0.1), fold=rng.uniform(22, 38) if not upright else rng.uniform(15, 30),
                                 side_card=side_card, upright=upright))

    def make_dead_branch(self, z, az, young, small=False):
        rng = self.rng
        tpt, tr = self.center(z), self.radius(min(z, self.top_z)) - self.bark_t
        L = rng.uniform(1.0, 2.2) if not young else rng.uniform(0.6, 1.4)
        if small:
            L *= 0.5
        L = min(L, self.R * 0.85)
        hd = Vector((math.cos(az), math.sin(az), 0))
        p = tpt + hd * (tr * 0.2)
        pts = [p.copy()]
        n = 5
        ph = math.radians(rng.uniform(-5, 10))
        a = az
        for i in range(n):
            ph -= math.radians(rng.uniform(3, 9))       # провисает к концу
            a += math.radians(rng.gauss(0, 7))
            d = Vector((math.cos(ph) * math.cos(a), math.cos(ph) * math.sin(a), math.sin(ph)))
            p = p + d * (L / n)
            pts.append(p.copy())
        r0 = min((0.8 + 0.9 * L) / 200.0, 0.3 * tr)
        radii = [max(0.003, r0 * (1 - 0.75 * i / n)) for i in range(n + 1)]
        br = Branch(pts, radii, [0.0] * (n + 1), 1, False, 'dead')
        br.cap = 'broken' if rng.random() < 0.55 else None
        br.depth = 1.0
        self.branches.append(br)
        for _ in range(rng.randint(2, 4)):   # голые сухие веточки, часть обломана
            s = rng.uniform(0.25, 0.9)
            q, d, r, w = br.sample(s)
            side = d.cross(UP).normalized() * rng.choice((-1, 1))
            ld = (d * 0.55 + side * 0.8 + UP * rng.uniform(-0.25, 0.35)).normalized()
            ll = rng.uniform(0.12, 0.5) * (1 - s * 0.5)
            q2 = q + ld * ll
            tw = Branch([q, (q + q2) * 0.5 - UP * ll * 0.05, q2], [max(0.002, r * 0.5), max(0.002, r * 0.35), 0.0018],
                        [0, 0, 0], 2, False, 'twig')
            tw.cap = 'none'
            br.children.append(tw)

    def make_stub(self, z, az, length, r_scale=1.0):
        rng = self.rng
        tpt, tr = self.center(z), self.radius(min(z, self.top_z))
        hd = Vector((math.cos(az), math.sin(az), 0))
        ph = math.radians(rng.uniform(-18, 10))
        d = Vector((math.cos(ph) * hd.x, math.cos(ph) * hd.y, math.sin(ph)))
        p0 = tpt + hd * (tr * 0.5)
        p1 = tpt + hd * tr + d * length
        r = min(rng.uniform(0.009, 0.02) * r_scale, 0.25 * tr)
        st = Branch([p0, p0.lerp(p1, 0.5), p1], [r, r * 0.92, r * 0.8], [0, 0, 0], 1, False, 'stub')
        st.cap = 'broken'
        self.stubs.append(st)

    def make_roots(self):
        """Поверхностные корни на песке: от корневых лап наружу, уходят в песок."""
        rng = self.rng
        n = rng.randint(4, 5) if not self.young else 3
        for i in range(n):
            th = self.flare_phase + (i + rng.uniform(-0.25, 0.25)) * math.tau / n
            r_tr = self.radius(0.15, th)
            L = rng.uniform(0.6, 1.4) * (self.dbh / 0.32) ** 0.5
            c = self.center(0.0)
            pts, radii = [], []
            a = th
            for k in range(5):
                s = k / 4
                a += rng.gauss(0, 0.08)
                rr = r_tr * 0.75 + L * s
                z = lerp(0.16, -0.07, s ** 0.8)
                pts.append(c + Vector((math.cos(a) * rr, math.sin(a) * rr, z)))
                radii.append(lerp(0.06, 0.018, s) * (self.dbh / 0.32))
            self.roots.append(Branch(pts, radii, [0.0] * 5, 1, False, 'root'))


# ------------------------------------------------------------------ меш

class PMesh:
    """Вершины + грани с атрибутами на углах: материал, UV0, UV1, цвет (альфа = ветер), нормаль."""

    def __init__(self):
        self.v = []
        self.f = []

    def vert(self, p):
        self.v.append((p[0], p[1], p[2]))
        return len(self.v) - 1

    def face(self, idx, mat, uv, col, nrm, uv1=None):
        self.f.append((tuple(idx), mat, list(uv), list(col), list(nrm), uv1 or [(0.0, 0.0)] * len(idx)))

    @property
    def tris(self):
        return sum(len(f[0]) - 2 for f in self.f)

    def to_object(self, name, materials, used_mats):
        """used_mats — индексы материалов этого LOD по порядку подмешей; индексы граней перенумеровываются."""
        remap = {m: i for i, m in enumerate(used_mats)}
        faces = [f for f in self.f if f[1] in remap]
        me = bpy.data.meshes.new(name)
        me.from_pydata(self.v, [], [f[0] for f in faces])
        me.update()
        nl = len(me.loops)
        uv0 = np.zeros((nl, 2), np.float32)
        uv1 = np.zeros((nl, 2), np.float32)
        col = np.zeros((nl, 4), np.float32)
        nrm = np.zeros((nl, 3), np.float32)
        k = 0
        mi = np.zeros(len(faces), np.int32)
        for fi, (idx, mat, uv, c, n, u1) in enumerate(faces):
            m = len(idx)
            uv0[k:k + m] = uv
            uv1[k:k + m] = u1
            col[k:k + m] = c
            nrm[k:k + m] = n
            mi[fi] = remap[mat]
            k += m
        assert k == nl, (k, nl)
        me.uv_layers.new(name='UVMap').uv.foreach_set('vector', uv0.ravel())
        me.uv_layers.new(name='UV2').uv.foreach_set('vector', uv1.ravel())
        ca = me.color_attributes.new(name='Col', type='BYTE_COLOR', domain='CORNER')
        ca.data.foreach_set('color', np.clip(col, 0, 1).ravel())
        for m in used_mats:
            me.materials.append(materials[m])
        mat_attr = me.attributes.get('material_index') or me.attributes.new('material_index', 'INT', 'FACE')
        mat_attr.data.foreach_set('value', mi)
        me.update()
        me.shade_smooth()
        nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-6)
        me.normals_split_custom_set([tuple(x) for x in nrm])
        ob = bpy.data.objects.new(name, me)
        eg.link(ob)
        return ob


def stitch_rings(mesh, r0, r1, add):
    """Сшивка двух колец (списки вершин с полем 'a' — угол 0..1, первая и последняя вершины — шов) «молнией»:
    r0 ниже, r1 выше; add(tri) добавляет треугольник (обход против часовой снаружи)."""
    i = j = 0
    na, nb = len(r0) - 1, len(r1) - 1
    while i < na or j < nb:
        if j >= nb or (i < na and r0[i + 1]['a'] <= r1[j + 1]['a']):
            add((r0[i], r0[i + 1], r1[j]))
            i += 1
        else:
            add((r0[i], r1[j + 1], r1[j]))
            j += 1


def zip_columns(A, B, add):
    """Полоса между двумя вертикальными цепочками вершин (A — колонка с меньшим углом, B — с большим), по z."""
    i = j = 0
    while i < len(A) - 1 or j < len(B) - 1:
        if j >= len(B) - 1 or (i < len(A) - 1 and A[i + 1]['z'] <= B[j + 1]['z']):
            add((A[i], B[j], A[i + 1]))
            i += 1
        else:
            add((A[i], B[j], B[j + 1]))
            j += 1


class TrunkUV:
    """V коры по высоте без растяжения: нижняя — плитка low_tile (м) при u_low повторах по окружности у DBH,
    лисья — квадратная плитка при u_up повторах на окружности у границы коры."""

    def __init__(self, tree):
        self.tree = tree
        r13 = tree.radius(1.3)
        self.u_low = max(1, round(2 * math.pi * r13 / tree.low_tile[0]))
        r_up = tree.radius(tree.P['rough_top']) - tree.bark_t
        self.u_up = max(1, round(2 * math.pi * r_up / BARK_UPPER_TILE))
        zs = np.linspace(-0.5, tree.H + 0.5, 600)
        circ = np.array([2 * math.pi * max(tree.radius(z), 0.01) for z in zs])
        dz = np.diff(zs)
        cm = 0.5 * (circ[1:] + circ[:-1])
        th = tree.low_tile[1] * (cm / self.u_low) / tree.low_tile[0]
        self.zs = zs
        self.vl = np.concatenate([[0.0], np.cumsum(dz / th)])
        self.vu = np.concatenate([[0.0], np.cumsum(dz * self.u_up / cm)]) + 0.37

    def v_low(self, z):
        return float(np.interp(z, self.zs, self.vl))

    def v_up(self, z):
        return float(np.interp(z, self.zs, self.vu))


def build_trunk(mesh, tree, cfg, lod):
    """Ствол: низ (n_base сторон, грубая кора) -> полоса перехода (n_band колонок, граница коры по вершинам) ->
    верх (лисья кора, n_up сторон до кроны, n_crown в кроне) до вершины/развилки."""
    H, P = tree.H, tree.P
    top = tree.top_z
    uv = TrunkUV(tree)
    nb = cfg['n_band']
    dth = math.tau / nb
    bnd = [tree.boundary(s / nb * math.tau, dth) for s in range(nb)]
    bnd.append(bnd[0])
    z_a = max(0.9, min(bnd) - 0.6)
    z_b = max(bnd) + 0.15
    t0 = tree.bark_t

    def vrec(theta_frac, z, r_out, mat_kind, inner=False, edge_t=None):
        """Вершина ствола: угол (доля), высота, внешний радиус; inner — по лисьей коре (тоньше на t0)."""
        th = theta_frac * math.tau
        c = tree.center(z)
        nxt = tree.center(min(z + 0.3, top))
        prv = tree.center(max(z - 0.3, -0.5))
        t = (nxt - prv).normalized()
        off = Vector((math.cos(th), math.sin(th), 0.0))
        off = (off - t * off.dot(t)).normalized()
        r = r_out
        if inner:
            r = r_out - t0
        if edge_t is not None:
            r = r_out - t0 + edge_t
        p = c + off * r
        dr = (tree.radius(z + 0.2) - tree.radius(z - 0.2)) / 0.4
        nrm = (off - t * dr).normalized()
        return dict(i=mesh.vert(p), a=theta_frac, z=z, n=nrm, kind=mat_kind)

    def add(tri):
        lower = all(c['kind'] == 'low' for c in tri)
        mat = M_LOWER if lower else M_UPPER
        a0 = tri[0]['a']
        uvs = []
        for c in tri:
            a = c['a']
            if a < 0.25 and a0 > 0.75:
                a += 1.0
            elif a > 0.75 and a0 < 0.25:
                a -= 1.0
            if lower:
                uvs.append((a * uv.u_low, uv.v_low(c['z'])))
            else:
                uvs.append((a * uv.u_up, uv.v_up(c['z'])))
        col = [(0, 0, 0, tree.trunk_wind(c['z'])) for c in tri]
        mesh.face([c['i'] for c in tri], mat, uvs, col, [c['n'] for c in tri],
                  [(0.0, max(c['z'], 0) / H) for c in tri])

    def ring(z, n, kind, inner=False):
        out = []
        for s in range(n + 1):
            a = s / n
            out.append(vrec(a, z, tree.radius(z, (s % n) / n * math.tau), kind, inner))
        out[-1] = dict(out[-1])
        out[-1]['i'] = out[0]['i']
        out[-1]['a'] = 1.0
        return out

    # --- низ ствола (грубая кора)
    zs_low = [-0.5, 0.0, 0.07, 0.18, 0.35, 0.6, 0.9]
    if lod >= 1:
        zs_low = [-0.5, 0.0, 0.15, 0.45, 0.9] if lod == 1 else [-0.5, 0.0, 0.3, 0.9]
    z = zs_low[-1]
    while z + cfg['ring_low'] < z_a - 0.2:
        z += cfg['ring_low']
        zs_low.append(z)
    zs_low.append(z_a)
    rings = [ring(z, cfg['n_base'], 'low') for z in zs_low[:-1]]
    band_bottom = []
    for s in range(nb + 1):
        a = s / nb
        band_bottom.append(vrec(a, z_a, tree.radius(z_a, (s % nb) / nb * math.tau), 'low'))
    band_bottom[-1]['i'] = band_bottom[0]['i']
    rings.append(band_bottom)
    for r0, r1 in zip(rings, rings[1:]):
        stitch_rings(mesh, r0, r1, add)
    # --- полоса перехода: колонки с вершиной ровно на границе коры
    band_z = []
    z = z_a + cfg['band_step']
    while z < z_b - 0.05:
        band_z.append(z)
        z += cfg['band_step']
    low_cols, up_cols = [], []
    for s in range(nb + 1):
        a = s / nb
        th = (s % nb) / nb * math.tau
        b = bnd[s]
        lc = [band_bottom[s]]
        for zz in band_z:
            if zz < b - 0.02:
                # к границе грубая кора тоньше: у самого края 35 % толщины
                et = t0 * (0.35 + 0.65 * smooth01((b - zz) / 0.5))
                lc.append(vrec(a, zz, tree.radius(zz, th), 'low', edge_t=et))
        lc.append(vrec(a, b, tree.radius(b, th), 'low', edge_t=t0 * 0.35))
        uc = [vrec(a, b, tree.radius(b, th), 'up', inner=True)]
        for zz in band_z:
            if zz > b + 0.02:
                uc.append(vrec(a, zz, tree.radius(zz, th), 'up', inner=True))
        uc.append(vrec(a, z_b, tree.radius(z_b, th), 'up', inner=True))
        if s == nb:   # шов: те же вершины, что у колонки 0
            for col_new, col_old in ((lc, low_cols[0]), (uc, up_cols[0])):
                for v_new, v_old in zip(col_new, col_old):
                    v_new['i'] = v_old['i']
                    v_new['a'] = 1.0
        low_cols.append(lc)
        up_cols.append(uc)
    for s in range(nb):
        zip_columns(low_cols[s], low_cols[s + 1], add)
        zip_columns(up_cols[s], up_cols[s + 1], add)
        # ступенька: торец грубой коры (внешняя граница -> внутренняя)
        Ls, Ls1 = low_cols[s][-1], low_cols[s + 1][-1]
        Us, Us1 = up_cols[s][0], up_cols[s + 1][0]
        a1 = Ls1['a'] if Ls1['a'] > Ls['a'] else Ls['a'] + 1.0 / nb
        wall_n = [(c['n'] * 0.3 + UP).normalized() for c in (Ls, Ls1, Us1, Us)]
        vb = uv.v_low(Ls['z'])
        mesh.face([Ls['i'], Ls1['i'], Us1['i'], Us['i']], M_LOWER,
                  [(Ls['a'] * uv.u_low, vb), (a1 * uv.u_low, uv.v_low(Ls1['z'])),
                   (a1 * uv.u_low, uv.v_low(Ls1['z']) - 0.006), (Ls['a'] * uv.u_low, vb - 0.006)],
                  [(0, 0, 0, tree.trunk_wind(Ls['z']))] * 4, wall_n,
                  [(0.0, Ls['z'] / H), (0.0, Ls1['z'] / H), (0.0, Us1['z'] / H), (0.0, Us['z'] / H)])
    band_top = [up_cols[s][-1] for s in range(nb + 1)]
    # --- верх ствола (лисья кора)
    zs_up = []
    z = z_b
    while True:
        step = cfg['ring_up'] if z < tree.zcb else cfg['ring_up'] * 0.6
        z += step
        if z >= top - 0.3:
            break
        zs_up.append(z)
    zs_up.append(top)
    prev = band_top
    for zz in zs_up:
        n = cfg['n_up'] if zz < tree.zcb + 0.5 else cfg['n_crown']
        r = ring(zz, n, 'up', inner=True)
        stitch_rings(mesh, prev, r, add)
        prev = r
    # верхушка — конус к вершине (у развилки закрыта лидерами)
    tip_z = top + (0.12 if not tree.fork_z else 0.0)
    tip = tree.center(top) + Vector((0, 0, tip_z - top))
    ti = mesh.vert(tip)
    vt = uv.v_up(top)
    for s in range(len(prev) - 1):
        a, b = prev[s], prev[s + 1]
        mesh.face([a['i'], b['i'], ti], M_UPPER, [(a['a'] * uv.u_up, vt), (b['a'] * uv.u_up, vt),
                                                 ((a['a'] + b['a']) * 0.5 * uv.u_up, vt + 0.3)],
                  [(0, 0, 0, tree.trunk_wind(top))] * 3, [a['n'], b['n'], UP], [(0, 1), (0, 1), (0, 1)])
    return uv, bnd


def resample(br, segs, s_end=1.0):
    """Ломаная ветви с segs отрезками (равномерно по длине) до доли s_end."""
    s_end = min(1.0, s_end)
    if segs >= len(br.pts) - 1 and s_end >= 1.0:
        return list(br.pts), list(br.radii), list(br.wind)
    pts, radii, wind = [], [], []
    for i in range(segs + 1):
        p, d, r, w = br.sample(i / segs * s_end)
        pts.append(p)
        radii.append(r)
        wind.append(w)
    return pts, radii, wind


def sweep(mesh, pts, radii, wind, sides, H, uvmode, tree, cap=None, rng=None, mat_fn=None):
    """Труба ветви: кольца по точкам, перенос рамки без скручивания.
    uvmode: 'upper' — лисья кора, квадратная плитка, целое число повторов по окружности у основания; 'fine' — нижняя
    кора мелко (серо-бурые побеги и ветви молодых: плитка 0.12 м по окружности); 'low' — нижняя кора как на стволе
    (корни); 'dead' — серая пластина нижней коры (прямоугольник deadwood_rect, зеркально по окружности и по длине:
    сухое серое дерево с продольными волокнами). mat_fn(i) -> материал отрезка i (по умолчанию по uvmode)."""
    n = len(pts)
    if n < 2 or sides < 3:
        return
    frames = []
    prev = None
    for i in range(n):
        t = (pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]).normalized()
        nx = t.orthogonal().normalized() if prev is None else (prev - t * prev.dot(t)).normalized()
        prev = nx
        frames.append((t, nx, t.cross(nx).normalized()))
    rect = tree.bark['deadwood_rect']
    low_tile = tree.low_tile

    def uv_at(mode, s_frac, along, circ0, i):
        if mode == 'dead':
            u = rect[0] + (rect[2] - rect[0]) * (1 - abs(2 * s_frac - 1))
            per = (rect[3] - rect[1]) * 1.2 * 6   # плитка нижней коры 1.2 м: серая пластина ~0.11 м -> растянута
            ph = (along / per) % 2.0
            v = rect[1] + (rect[3] - rect[1]) * (ph if ph <= 1.0 else 2.0 - ph)
            return u, v
        if mode == 'upper':
            urep = max(1, round(circ0 / BARK_UPPER_TILE))
            return s_frac * urep, along * urep / max(circ0, 1e-3) + 0.13
        if mode == 'fine':
            # плитка нижней коры (пропорции 1:2) на tw м по окружности: мелкие серо-бурые чешуйки побегов
            tw = 0.12
            urep = max(1, round(circ0 / tw))
            return s_frac * urep, along / (2 * tw) + 0.29
        urep = max(1, round(circ0 / low_tile[0]))
        return s_frac * urep, along / low_tile[1]

    circ0 = 2 * math.pi * max(radii[0], 0.002)
    along = [0.0]
    for i in range(1, n):
        along.append(along[-1] + (pts[i] - pts[i - 1]).length)
    rings = []
    for i in range(n):
        t, nx, ny = frames[i]
        r = max(radii[i], 0.0015)
        ring = []
        for s in range(sides + 1):
            a = s / sides * math.tau
            off = nx * math.cos(a) + ny * math.sin(a)
            ring.append((mesh.vert(pts[i] + off * r) if s < sides else None, s / sides, off))
        ring[-1] = (ring[0][0], 1.0, ring[0][2])
        rings.append(ring)
    default_mat = M_UPPER if uvmode == 'upper' else M_LOWER
    for i in range(n - 1):
        w0, w1 = wind[i], wind[i + 1]
        mat = mat_fn(i) if mat_fn else default_mat
        mode = uvmode if mat == default_mat else ('fine' if mat == M_LOWER else 'upper')
        for s in range(sides):
            a, b, c, d = rings[i][s], rings[i][s + 1], rings[i + 1][s + 1], rings[i + 1][s]
            uvs = [uv_at(mode, a[1], along[i], circ0, i), uv_at(mode, b[1], along[i], circ0, i),
                   uv_at(mode, c[1], along[i + 1], circ0, i), uv_at(mode, d[1], along[i + 1], circ0, i)]
            mesh.face([a[0], b[0], c[0], d[0]], mat, uvs,
                      [(0, 0, 0, w0), (0, 0, 0, w0), (0, 0, 0, w1), (0, 0, 0, w1)], [a[2], b[2], c[2], d[2]],
                      [(0, pts[i].z / H)] * 2 + [(0, pts[i + 1].z / H)] * 2)
    t = frames[-1][0]
    last = rings[-1]
    mat_end = mat_fn(n - 2) if mat_fn else default_mat
    end_mode = uvmode if mat_end == default_mat else ('fine' if mat_end == M_LOWER else 'upper')
    if cap == 'broken' and rng is not None:
        # обломок: зубцы разной длины, торец — светлое сухое дерево (серая пластина)
        r = radii[-1]
        teeth = [mesh.vert(pts[-1] + t * (r * rng.uniform(0.2, 1.8)) + last[s][2] * (r * 0.3)) for s in range(sides)]
        ci = mesh.vert(pts[-1] + t * (r * 0.5))
        rc = 0.5 * (rect[0] + rect[2]), 0.5 * (rect[1] + rect[3])
        for s in range(sides):
            a, b = last[s], last[s + 1]
            ta, tb = teeth[s], teeth[(s + 1) % sides]
            ua, ub = uv_at('dead', a[1], along[-1], circ0, 0), uv_at('dead', b[1], along[-1], circ0, 0)
            mesh.face([a[0], b[0], tb, ta], M_LOWER, [ua, ub, (ub[0], ub[1] + 0.01), (ua[0], ua[1] + 0.01)],
                      [(0, 0, 0, wind[-1])] * 4, [a[2], b[2], t, t], [(0, pts[-1].z / H)] * 4)
            mesh.face([ta, tb, ci], M_LOWER, [(rc[0] - 0.01, rc[1]), (rc[0] + 0.01, rc[1]), (rc[0], rc[1] + 0.01)],
                      [(0, 0, 0, wind[-1])] * 3, [t, t, t], [(0, pts[-1].z / H)] * 3)
    elif cap != 'none' and radii[-1] > 0.0025:
        ti = mesh.vert(pts[-1] + t * radii[-1] * 1.5)
        for s in range(sides):
            a, b = last[s], last[s + 1]
            ua, ub = uv_at(end_mode, a[1], along[-1], circ0, 0), uv_at(end_mode, b[1], along[-1], circ0, 0)
            mesh.face([a[0], b[0], ti], mat_end, [ua, ub, (ua[0], ua[1] + 0.02)], [(0, 0, 0, wind[-1])] * 3,
                      [a[2], b[2], t], [(0, pts[-1].z / H)] * 3)


# ------------------------------------------------------------------ карточки хвои

def card_frame(tree, sprite, scale, p, d, n):
    """Положение спрайта: точка крепления p (anchor спрайта), ось d (вверх по спрайту), нормаль ~n."""
    spr = tree.atlas[sprite]
    w_m, h_m = spr['size'][0] * scale, spr['size'][1] * scale
    au, av = spr['anchor']
    U = d.cross(n).normalized()
    nn = U.cross(d).normalized()
    return spr, w_m, h_m, au, av, U, nn


def emit_card(mesh, tree, p, d, n, sprite, scale, w, pad_c, crown_c, fold=0.0):
    """Карточка со сгибом по оси (крыша, крылья вниз на fold°): ребром не пропадает; 4 треугольника (2 без сгиба).
    Нормали: 35 % карточки (наружу от центра подушки), 40 % от центра подушки, 25 % от центра кроны, чуть вверх."""
    spr, w_m, h_m, au, av, U, nn = card_frame(tree, sprite, scale, p, d, n)
    u0, v0, u1, v1 = spr['uv']
    ua = lerp(u0, u1, au)
    f = math.radians(fold)
    left = (-U * math.cos(f) - nn * math.sin(f)) * (au * w_m)
    right = (U * math.cos(f) - nn * math.sin(f)) * ((1 - au) * w_m)
    b0 = p - d * (av * h_m)
    ext = d * h_m
    pts = [b0 + left, b0, b0 + right, b0 + right + ext, b0 + ext, b0 + left + ext]
    uvs = [(u0, v0), (ua, v0), (u1, v0), (u1, v1), (ua, v1), (u0, v1)]
    wt = min(0.85, w + 0.04)
    cols = [(w, w, w, w)] * 3 + [(wt, wt, wt, wt)] * 3
    face_n = nn if nn.z >= 0 else -nn
    nrms = []
    for q in pts:
        pr = q - pad_c
        pr = pr.normalized() if pr.length > 1e-4 else UP
        cr = q - crown_c
        cr = cr.normalized() if cr.length > 1e-4 else UP
        fn = face_n if face_n.dot(pr) >= -0.2 else -face_n
        nrms.append((fn * 0.35 + pr * 0.4 + cr * 0.25 + UP * 0.12).normalized())
    idx = [mesh.vert(q) for q in pts]
    uv1 = [(0, q.z / tree.H) for q in pts]
    if fold <= 0.01:
        sel = (0, 2, 3, 5)
        mesh.face([idx[i] for i in sel], M_LEAVES, [uvs[i] for i in sel], [cols[i] for i in sel],
                  [nrms[i] for i in sel], [uv1[i] for i in sel])
        return
    for quad in ((0, 1, 4, 5), (1, 2, 3, 4)):
        mesh.face([idx[i] for i in quad], M_LEAVES, [uvs[i] for i in quad], [cols[i] for i in quad],
                  [nrms[i] for i in quad], [uv1[i] for i in quad])


def emit_side_card(mesh, tree, sp, pad_c, crown_c, scale_k=1.0):
    """Вертикальная карточка-профиль веера (вид сбоку): плоскость оси веера и его нормали."""
    d = sp['d']
    U = sp['n'].copy()     # «вправо» на виде сбоку — нормаль веера (вверх)
    n_side = d.cross(U).normalized()
    emit_card(mesh, tree, sp['p'], d, n_side, 'spray_side', sp['scale'] * scale_k, sp['w'], pad_c, crown_c, 0.0)


def all_sprays(tree):
    out = []
    for bi, br in enumerate(tree.branches):
        if not br.alive:
            continue
        for s in br.sprays:
            out.append((bi, s))
        for c in br.children:
            for s in c.sprays:
                out.append((bi, s))
    return out


def spray_centre(tree, sp):
    spr = tree.atlas[sp['sprite']]
    reach = (1 - spr['anchor'][1]) * spr['size'][1] * sp['scale']
    return sp['p'] + sp['d'] * (reach * 0.5)


def pad_centres(tree):
    pads = {}
    for bi, s in all_sprays(tree):
        pads.setdefault(bi, []).append(spray_centre(tree, s))
    return {bi: sum(v, Vector()) / len(v) for bi, v in pads.items()}


def crown_centre(tree):
    sp = all_sprays(tree)
    if not sp:
        return tree.center(lerp(tree.zcb, tree.H, 0.55))
    c = sum((spray_centre(tree, s) for _, s in sp), Vector()) / len(sp)
    return c


def opaque_area(tree, sprite, scale):
    spr = tree.atlas[sprite]
    return spr['size'][0] * spr['size'][1] * spr['coverage'] * scale * scale


def thin_sprays(tree, keep):
    """Оставить долю keep вееров (убираются мелкие нижние/внутренние), остальные укрупнить с сохранением площади."""
    rng = random.Random(tree.P['seed'] + 123)
    owners = []
    for b in tree.branches:
        if b.alive:
            owners.append(b)
            owners += b.children
    items = [(o, s) for o in owners for s in o.sprays]
    area0 = sum(opaque_area(tree, s['sprite'], s['scale']) for _, s in items)
    cand = [x for x in items if x[1]['sprite'] != 'spray_large' or x[1]['scale'] < 1.0]
    if len(cand) < len(items) * (1 - keep):
        cand = list(items)
    rng.shuffle(cand)
    kill = set(id(x[1]) for x in cand[:int(len(items) * (1 - keep))])
    for o in owners:
        o.sprays = [s for s in o.sprays if id(s) not in kill]
    left = [s for _, s in items if id(s) not in kill]
    k = min(1.08, math.sqrt(area0 / max(sum(opaque_area(tree, s['sprite'], s['scale']) for s in left), 1e-6)))
    for s in left:
        s['scale'] = min(1.6, s['scale'] * k)


def emit_foliage(mesh, tree, lod, rng, area_k):
    sprays = all_sprays(tree)
    pads = pad_centres(tree)
    crown_c = crown_centre(tree)
    if lod == 0:
        for bi, s in sprays:
            emit_card(mesh, tree, s['p'], s['d'], s['n'], s['sprite'], s['scale'], s['w'], pads[bi], crown_c,
                      fold=s['fold'])
            if s['side_card']:
                emit_side_card(mesh, tree, s, pads[bi], crown_c)
        return
    by_br = {}
    for bi, sp in sprays:
        by_br.setdefault(bi, []).append(sp)
    group = 2.6 if lod == 1 else 9.0
    cluster_cards(mesh, tree, rng, by_br, pads, crown_c, group, area_k, lod)


def cluster_cards(mesh, tree, rng, by_br, pads, crown_c, group, area_k, lod):
    """Вееры каждой ветви группируются (k-средних по центрам) по ~group штук; группа -> одна карточка той же площади
    хвои (area_k — поправка на перекрытие, подогнана по силуэту LOD0), с тем же центром, средней осью, нормалью и
    сгибом; вертикальный профиль — у той же доли групп, что и у вееров LOD0."""
    for bi, lst in by_br.items():
        items = []
        for sp in lst:
            items.append((spray_centre(tree, sp), sp, opaque_area(tree, sp['sprite'], sp['scale'])))
        k = max(1, int(round(len(items) / group)))
        centres = [c[0] for c in rng.sample(items, k)]
        groups = []
        for _ in range(8):
            groups = [[] for _ in range(k)]
            for it in items:
                j = min(range(k), key=lambda i: (it[0] - centres[i]).length)
                groups[j].append(it)
            centres = [sum((g[0] * g[2] for g in grp), Vector()) / max(sum(g[2] for g in grp), 1e-6)
                       if grp else centres[i] for i, grp in enumerate(groups)]
        for grp, c in zip(groups, centres):
            if not grp:
                continue
            area = sum(g[2] for g in grp)
            d = sum((g[1]['d'] * g[2] for g in grp), Vector()).normalized()
            # крупная карточка повторяет огибающую подушки (плоский эллипсоид): ось положе, нормаль ближе к вертикали
            d = Vector((d.x, d.y, d.z * (0.8 if lod == 1 else 0.65))).normalized()
            n = sum((g[1]['n'] * g[2] for g in grp), Vector()).normalized() + UP * (0.5 if lod == 1 else 1.0)
            n = (n - d * n.dot(d)).normalized() if n.length > 1e-6 else UP
            fold = sum(g[1]['fold'] * g[2] for g in grp) / area * (0.7 if lod == 1 else 0.45)
            w = sum(g[1]['w'] * g[2] for g in grp) / area
            side = d.cross(n).normalized()
            al = [(g[0] - c).dot(d) for g in grp]
            ext_d = (max(al) - min(al)) + max(g[2] for g in grp) ** 0.5 * 1.3
            if len(grp) == 1 and lod == 1:
                sp = grp[0][1]
                emit_card(mesh, tree, sp['p'], sp['d'], sp['n'], sp['sprite'], sp['scale'] * area_k ** 0.5, sp['w'],
                          pads[bi], crown_c, fold=sp['fold'])
                continue
            # тот же рисунок, что у вееров LOD0 (тон и плотность те же), крупнее — хвоинок на этих расстояниях не видно
            sprite = 'spray_b' if rng.random() < 0.35 else 'spray_large'
            spr = tree.atlas[sprite]
            unit = spr['size'][0] * spr['size'][1] * spr['coverage']
            sc_a = math.sqrt(area * area_k / unit)
            sc_e = ext_d * 1.1 / max(spr['size'][1] * (1 - spr['anchor'][1]) * 0.9, 1e-3)
            sc = sc_a if sc_a <= sc_e * 1.4 else math.sqrt(sc_a * sc_e * 1.4)
            # центр непрозрачного спрайта — в центр группы
            cu, cv = spr.get('centroid', (0.5, 0.5))
            au, av = spr['anchor']
            w_m, h_m = spr['size'][0] * sc, spr['size'][1] * sc
            p = c - side * ((cu - au) * w_m) - d * ((cv - av) * h_m)
            emit_card(mesh, tree, p, d, n, sprite, sc, w, pads[bi], crown_c, fold=fold)
            # крутые карточки (профили вееров и щётки вверх) — той же доли площади, что у LOD0
            steep = sum(g[2] * (1.0 if g[1]['side_card'] else 0.0) + g[2] * (1.0 if g[1]['upright'] else 0.0)
                        for g in grp) / area
            if rng.random() < min(1.0, steep * 2.2):
                spv = dict(p=c - d * (h_m * 0.35), d=d, n=n, scale=sc * 0.75, w=w)
                emit_side_card(mesh, tree, spv, pads[bi], crown_c)


def build_lod(tree, lod, area_k=1.0):
    cfg = LOD_CFG[lod]
    mesh = PMesh()
    rng = random.Random(tree.P['seed'] + 17 * lod)
    H = tree.H
    parts = {}
    t0 = mesh.tris
    build_trunk(mesh, tree, cfg, lod)
    parts['trunk'] = mesh.tris - t0
    for ld in tree.leaders:
        pts, radii, wind = resample(ld, max(2, cfg['prim_segs']))
        sweep(mesh, pts, radii, wind, max(4, cfg['n_crown']), H, 'upper', tree)
    t0 = mesh.tris
    young = tree.young
    live = [b for b in tree.branches if b.alive]
    if lod == 2:
        live = sorted(live, key=lambda b: -b.length)[:max(8, len(live) * 2 // 3)]
    for br in live:
        segs = cfg['prim_segs']
        s_end = 1.0
        pts, radii, wind = resample(br, segs, s_end)
        sides = cfg['prim_sides'][0] if br.radii[0] > 0.025 else cfg['prim_sides'][1]
        if young:
            sweep(mesh, pts, radii, wind, sides, H, 'fine', tree)
        else:
            # толстая часть — лисья кора, внешняя тонкая (подушка) — серо-бурые побеги
            s_split = max(br.s_pad, 0.55)
            sweep(mesh, pts, radii, wind, sides, H, 'upper', tree,
                  mat_fn=lambda i, n=len(pts), s=s_split: M_LOWER if (i + 0.5) / (n - 1) > s else M_UPPER)
    parts['primary'] = mesh.tris - t0
    t0 = mesh.tris
    if cfg['lat_sides'] >= 3:
        for br in live:
            lats = sorted([c for c in br.children if c.kind == 'lat' and c.length >= cfg['lat_min']],
                          key=lambda c: -c.length)[:cfg['lat_max_n']]
            for lat in lats:
                # видна только часть до веера (дальше её рисует сам веер атласа)
                pts, radii, wind = resample(lat, 1, 0.5)
                sweep(mesh, pts, radii, wind, cfg['lat_sides'], H, 'fine', tree, cap='none')
            if cfg['twigs']:
                for tw in [c for c in br.children if c.kind == 'twig']:
                    sweep(mesh, tw.pts, tw.radii, tw.wind, 3, H, 'dead', tree, cap=tw.cap, rng=rng)
    parts['secondary'] = mesh.tris - t0
    t0 = mesh.tris
    if cfg['dead_sides'] >= 3:
        for br in [b for b in tree.branches if not b.alive]:
            segs = 4 if lod == 0 else 2
            pts, radii, wind = resample(br, segs)
            sweep(mesh, pts, radii, wind, cfg['dead_sides'], H, 'dead', tree, cap=br.cap if lod == 0 else None, rng=rng)
            if cfg['twigs']:
                for tw in br.children:
                    sweep(mesh, tw.pts, tw.radii, tw.wind, 3, H, 'dead', tree, cap=tw.cap, rng=rng)
    parts['dead'] = mesh.tris - t0
    t0 = mesh.tris
    if cfg['stubs']:
        stubs = sorted(tree.stubs, key=lambda s: -s.length * s.radii[0])[:cfg['stubs']]
        for st in stubs:
            sweep(mesh, [st.pts[0], st.pts[-1]], [st.radii[0], st.radii[-1]], [0, 0], 4 if lod == 0 else 3, H, 'dead',
                  tree, cap='broken' if lod == 0 else None, rng=rng)
    parts['stubs'] = mesh.tris - t0
    t0 = mesh.tris
    if cfg['roots']:
        for rt in tree.roots[:cfg['roots']]:
            pts, radii, wind = resample(rt, 4 if lod == 0 else 2)
            sweep(mesh, pts, radii, wind, 5 if lod == 0 else 3, H, 'low', tree)
    parts['roots'] = mesh.tris - t0
    bark_tris = mesh.tris
    emit_foliage(mesh, tree, lod, rng, area_k)
    parts['cards'] = mesh.tris - bark_tris
    tree.parts = getattr(tree, 'parts', {})
    tree.parts[lod] = parts
    return mesh, bark_tris


def card_orientation(mesh):
    """Доли граней хвои по углу нормали от вертикали: <30°, 30-60°, >60° (по площади)."""
    v = np.array(mesh.v)
    acc = np.zeros(3)
    for idx, mat, *_ in mesh.f:
        if mat != M_LEAVES:
            continue
        p = v[list(idx)]
        n = np.cross(p[1] - p[0], p[2] - p[0])
        a = np.linalg.norm(n)
        if a < 1e-9:
            continue
        ang = math.degrees(math.acos(min(1.0, abs(n[2]) / a)))
        acc[0 if ang < 30 else (1 if ang < 60 else 2)] += a
    return [round(float(x), 3) for x in acc / max(acc.sum(), 1e-9)]


# ------------------------------------------------------------------ материалы Blender (для рендера креста и превью)

def setup_gpu(scene):
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        prefs.compute_device_type = 'CUDA'
        prefs.get_devices()
        for d in prefs.devices:
            d.use = d.type == 'CUDA'
        scene.cycles.device = 'GPU'
    except Exception:
        scene.cycles.device = 'CPU'


def bark_material(name, albedo, normal):
    """Кора для превью: Principled, альбедо + нормаль."""
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    N.clear()
    out = N.new('ShaderNodeOutputMaterial')
    uv = N.new('ShaderNodeUVMap')
    uv.uv_map = 'UVMap'
    tex = N.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(albedo, check_existing=True)
    L.new(uv.outputs[0], tex.inputs[0])
    ni = N.new('ShaderNodeTexImage')
    ni.image = bpy.data.images.load(normal, check_existing=True)
    ni.image.colorspace_settings.name = 'Non-Color'
    L.new(uv.outputs[0], ni.inputs[0])
    nm = N.new('ShaderNodeNormalMap')
    nm.uv_map = 'UVMap'
    L.new(ni.outputs['Color'], nm.inputs['Color'])
    bsdf = N.new('ShaderNodeBsdfPrincipled')
    L.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    L.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    bsdf.inputs['Roughness'].default_value = 0.8
    L.new(bsdf.outputs[0], out.inputs['Surface'])
    return m


def card_material(name, albedo, cutoff=0.5, normal=None, cull_back=False, use_attr_normal=True):
    """Карточки хвои/креста для превью. Хвоя: нормаль — из атрибута 'nrm' (нормали вершин) на обеих сторонах, как у
    двусторонних материалов в Unity (Cycles сам переворачивает нормаль у изнанки), плюс просвет. Крест: карта
    нормалей в касательном пространстве (как в NM_Cross), изнанка прозрачна (Cull Back)."""
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    N.clear()
    out = N.new('ShaderNodeOutputMaterial')
    tex = N.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(albedo, check_existing=True)
    uv = N.new('ShaderNodeUVMap')
    uv.uv_map = 'UVMap'
    L.new(uv.outputs[0], tex.inputs[0])
    bsdf = N.new('ShaderNodeBsdfPrincipled')
    bsdf.inputs['Roughness'].default_value = 0.7
    L.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    if normal:
        ni = N.new('ShaderNodeTexImage')
        ni.image = bpy.data.images.load(normal, check_existing=True)
        ni.image.colorspace_settings.name = 'Non-Color'
        L.new(uv.outputs[0], ni.inputs[0])
        nm = N.new('ShaderNodeNormalMap')
        nm.uv_map = 'UVMap'
        L.new(ni.outputs['Color'], nm.inputs['Color'])
        L.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
    elif use_attr_normal:
        at = N.new('ShaderNodeAttribute')
        at.attribute_name = 'nrm'
        at.attribute_type = 'GEOMETRY'
        vt = N.new('ShaderNodeVectorTransform')
        vt.vector_type = 'NORMAL'
        vt.convert_from = 'OBJECT'
        vt.convert_to = 'WORLD'
        L.new(at.outputs['Vector'], vt.inputs['Vector'])
        L.new(vt.outputs['Vector'], bsdf.inputs['Normal'])
    tr = N.new('ShaderNodeBsdfTranslucent')
    L.new(tex.outputs['Color'], tr.inputs['Color'])
    mx = N.new('ShaderNodeMixShader')
    mx.inputs['Fac'].default_value = 0.2 if not cull_back else 0.1
    L.new(bsdf.outputs[0], mx.inputs[1])
    L.new(tr.outputs[0], mx.inputs[2])
    gt = N.new('ShaderNodeMath')
    gt.operation = 'GREATER_THAN'
    gt.inputs[1].default_value = cutoff
    L.new(tex.outputs['Alpha'], gt.inputs[0])
    fac = gt.outputs[0]
    if cull_back:
        geo = N.new('ShaderNodeNewGeometry')
        inv = N.new('ShaderNodeMath')
        inv.operation = 'SUBTRACT'
        inv.inputs[0].default_value = 1.0
        L.new(geo.outputs['Backfacing'], inv.inputs[1])
        mul = N.new('ShaderNodeMath')
        mul.operation = 'MULTIPLY'
        L.new(gt.outputs[0], mul.inputs[0])
        L.new(inv.outputs[0], mul.inputs[1])
        fac = mul.outputs[0]
    trn = N.new('ShaderNodeBsdfTransparent')
    mx2 = N.new('ShaderNodeMixShader')
    L.new(fac, mx2.inputs['Fac'])
    L.new(trn.outputs[0], mx2.inputs[1])
    L.new(mx.outputs[0], mx2.inputs[2])
    L.new(mx2.outputs[0], out.inputs['Surface'])
    return m


def pass_material(name, albedo, cutoff=None):
    """Проходы рендера креста: узел PASS 0 — альбедо, 1 — белый диффуз (небесная затенённость), 2 — нормаль камеры,
    3 — белая эмиссия (силуэт)."""
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    N.clear()
    out = N.new('ShaderNodeOutputMaterial')
    uv = N.new('ShaderNodeUVMap')
    uv.uv_map = 'UVMap'
    tex = N.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(albedo, check_existing=True)
    L.new(uv.outputs[0], tex.inputs[0])
    geo = N.new('ShaderNodeNewGeometry')
    at = N.new('ShaderNodeAttribute')
    at.attribute_name = 'nrm'
    at.attribute_type = 'GEOMETRY'
    nsrc = at.outputs['Vector'] if cutoff is not None else geo.outputs['Normal']
    if cutoff is not None:
        vt0 = N.new('ShaderNodeVectorTransform')
        vt0.vector_type = 'NORMAL'
        vt0.convert_from = 'OBJECT'
        vt0.convert_to = 'WORLD'
        L.new(at.outputs['Vector'], vt0.inputs['Vector'])
        nsrc = vt0.outputs['Vector']
    em = N.new('ShaderNodeEmission')
    dif = N.new('ShaderNodeBsdfDiffuse')
    dif.inputs['Color'].default_value = (0.8, 0.8, 0.8, 1)
    L.new(nsrc, dif.inputs['Normal'])
    pas = N.new('ShaderNodeValue')
    pas.name = 'PASS'
    vt = N.new('ShaderNodeVectorTransform')
    vt.vector_type = 'NORMAL'
    vt.convert_from = 'WORLD'
    vt.convert_to = 'CAMERA'
    L.new(nsrc, vt.inputs['Vector'])
    enc = N.new('ShaderNodeVectorMath')
    enc.operation = 'MULTIPLY_ADD'
    enc.inputs[1].default_value = (0.5, 0.5, -0.5)
    enc.inputs[2].default_value = (0.5, 0.5, 0.5)
    L.new(vt.outputs['Vector'], enc.inputs[0])
    isn = N.new('ShaderNodeMath')
    isn.operation = 'COMPARE'
    isn.inputs[1].default_value = 2.0
    isn.inputs[2].default_value = 0.1
    L.new(pas.outputs[0], isn.inputs[0])
    iss = N.new('ShaderNodeMath')
    iss.operation = 'COMPARE'
    iss.inputs[1].default_value = 3.0
    iss.inputs[2].default_value = 0.1
    L.new(pas.outputs[0], iss.inputs[0])
    cm = N.new('ShaderNodeMix')
    cm.data_type = 'RGBA'
    L.new(isn.outputs[0], cm.inputs['Factor'])
    L.new(tex.outputs['Color'], cm.inputs['A'])
    L.new(enc.outputs['Vector'], cm.inputs['B'])
    cw = N.new('ShaderNodeMix')
    cw.data_type = 'RGBA'
    L.new(iss.outputs[0], cw.inputs['Factor'])
    L.new(cm.outputs['Result'], cw.inputs['A'])
    cw.inputs['B'].default_value = (1, 1, 1, 1)
    L.new(cw.outputs['Result'], em.inputs['Color'])
    isd = N.new('ShaderNodeMath')
    isd.operation = 'COMPARE'
    isd.inputs[1].default_value = 1.0
    isd.inputs[2].default_value = 0.1
    L.new(pas.outputs[0], isd.inputs[0])
    sm = N.new('ShaderNodeMixShader')
    L.new(isd.outputs[0], sm.inputs['Fac'])
    L.new(em.outputs[0], sm.inputs[1])
    L.new(dif.outputs[0], sm.inputs[2])
    surf = sm.outputs[0]
    if cutoff is not None:
        gt = N.new('ShaderNodeMath')
        gt.operation = 'GREATER_THAN'
        gt.inputs[1].default_value = cutoff
        L.new(tex.outputs['Alpha'], gt.inputs[0])
        trn = N.new('ShaderNodeBsdfTransparent')
        mx2 = N.new('ShaderNodeMixShader')
        L.new(gt.outputs[0], mx2.inputs['Fac'])
        L.new(trn.outputs[0], mx2.inputs[1])
        L.new(surf, mx2.inputs[2])
        surf = mx2.outputs[0]
    L.new(surf, out.inputs['Surface'])
    return m


def set_pass(mats, p):
    for m in mats:
        m.node_tree.nodes['PASS'].outputs[0].default_value = float(p)


def add_nrm_attr(obj):
    me = obj.data
    if 'nrm' in me.attributes:
        me.attributes.remove(me.attributes['nrm'])
    at = me.attributes.new('nrm', 'FLOAT_VECTOR', 'CORNER')
    vals = np.zeros(len(me.loops) * 3, np.float32)
    me.corner_normals.foreach_get('vector', vals)
    at.data.foreach_set('vector', vals)


def remove_nrm_attr(obj):
    me = obj.data
    if 'nrm' in me.attributes:
        me.attributes.remove(me.attributes['nrm'])


# ------------------------------------------------------------------ рендеры для креста и проверок

def ortho_views(obj_bounds, top_view=True):
    """Кадры для 4 видов сбоку (с -Y, +X, +Y, -X) и сверху по вершинам (N, 3)."""
    vs = obj_bounds
    top = float(vs[:, 2].max())
    S = top * 1.02
    xc = 0.5 * (vs[:, 0].min() + vs[:, 0].max())
    yc = 0.5 * (vs[:, 1].min() + vs[:, 1].max())
    need = max(vs[:, 0].max() - vs[:, 0].min(), vs[:, 1].max() - vs[:, 1].min()) * 1.04
    Sw = S * CROSS_CELL[0] / CROSS_CELL[1]
    if need > Sw:
        S = need * CROSS_CELL[1] / CROSS_CELL[0]
        Sw = need
    return S, Sw, xc, yc


def render_views(scene, obj_list, mats_pass, S, Sw, xc, yc, top_box, tmp, tag, passes=(0, 1, 2), res=CROSS_CELL,
                 top_res=512, samples=48):
    """Рендеры видов: 4 сбоку (ортокамера, кадр по высоте 0..S, по ширине Sw вокруг центра) и сверху (квадрат
    top_box = (cx, cy, size)). Возвращает {вид: [массивы по проходам]}."""
    scene.cycles.samples = samples
    out = {}
    for k in range(5):
        if k < 4:
            a = k * math.pi / 2
            right = Vector((math.cos(a), math.sin(a), 0))
            c = right * (xc * right.x + yc * right.y)
            loc = Vector((math.sin(a) * 400, -math.cos(a) * 400, S * 0.5)) + Vector((c.x, c.y, 0))
            cam = eg.ortho_camera(scene, loc, (math.pi / 2, 0, a), S, name=f'xcam{k}')
            scene.render.resolution_x, scene.render.resolution_y = res
        else:
            cx, cy, size = top_box
            cam = eg.ortho_camera(scene, (cx, cy, 400.0), (0, 0, 0), size, name='xcam_top')
            scene.render.resolution_x = scene.render.resolution_y = top_res
        cam.data.clip_end = 2000
        arr = []
        for p in passes:
            set_pass(mats_pass, p)
            path = os.path.join(tmp, f'{tag}_{k}_{p}.png')
            scene.render.filepath = path
            bpy.ops.render.render(write_still=True)
            arr.append(eg.srgb_png_to_linear_values(path))
        bpy.data.objects.remove(cam)
        out[k] = arr
    return out


def card_scene(scene, samples):
    eg.setup_card_render(scene, CROSS_CELL[0], CROSS_CELL[1], samples=samples)
    setup_gpu(scene)
    scene.cycles.transparent_max_bounces = 96
    scene.cycles.max_bounces = 2
    scene.cycles.diffuse_bounces = 1
    world = bpy.data.worlds.get('cross_w') or bpy.data.worlds.new('cross_w')
    scene.world = world
    bg = world.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (1, 1, 1, 1)
    bg.inputs['Strength'].default_value = 1.0


def bake_cross(tree, obj, pass_mats, folder, name):
    """Атлас креста 2048 x 512: 4 вида сбоку по 384 x 512 (альбедо с запечённой небесной затенённостью — в игре
    вдали теней нет, — и нормаль: 65 % отрендеренной, 35 % «к камере и вверх») и вид сверху 512 x 512 справа."""
    scene = bpy.context.scene
    card_scene(scene, 48)
    for s, m in zip(obj.material_slots, pass_mats):
        s.material = m
    vs = np.array([obj.matrix_world @ v.co for v in obj.data.vertices])
    S, Sw, xc, yc = ortho_views(vs)
    zcb = tree.zcb
    crown = vs[vs[:, 2] > zcb - 0.5]
    tx0, tx1 = crown[:, 0].min(), crown[:, 0].max()
    ty0, ty1 = crown[:, 1].min(), crown[:, 1].max()
    tsize = max(tx1 - tx0, ty1 - ty0) * 1.04
    top_box = (0.5 * (tx0 + tx1), 0.5 * (ty0 + ty1), tsize)
    tmp = os.path.join(folder, '_cross_tmp')
    os.makedirs(tmp, exist_ok=True)
    views = render_views(scene, [obj], pass_mats, S, Sw, xc, yc, top_box, tmp, name)
    cw, ch = CROSS_CELL
    al = np.zeros((512, 2048, 4), np.float32)
    nm = np.zeros((512, 2048, 3), np.float32)
    for k in range(5):
        alb, ao, nrm = views[k]
        irr = np.clip(ao[..., 0] / 0.8, 0, 1)
        shade = 0.45 + 0.5 * irr ** 0.8
        c = alb.copy()
        c[..., :3] *= shade[..., None]
        if k < 4:
            al[:, k * cw:(k + 1) * cw] = c
            nm[:, k * cw:(k + 1) * cw] = nrm[..., :3]
        else:
            al[:, 4 * cw:] = c
            nm[:, 4 * cw:] = nrm[..., :3]
    a = al[..., 3]
    soft = np.array([0.5, 0.62, 0.9], np.float32)
    nm = np.where(a[..., None] > 0.01, nm * 0.65 + soft * 0.35, np.array([0.5, 0.5, 1.0], np.float32))
    al = eg.dilate_rgb(al, 8)
    shutil.rmtree(tmp, ignore_errors=True)
    return dict(albedo=al, normal=nm, S=S, Sw=Sw, xc=xc, yc=yc, top_box=top_box, alpha=a)


def save_cross(bake, folder, name):
    names = [f'{name}_cross_albedo.png', f'{name}_cross_normal.png']
    eg.save_png(bake['albedo'], os.path.join(folder, names[0]))
    eg.save_png(bake['normal'], os.path.join(folder, names[1]), linear=True)
    return names


def cross_mesh(tree, bake):
    """Крест: плоскость P0 (виды 0/2, проекция вдоль Y) и P1 (виды 1/3, вдоль X), у каждой две стороны (материал
    Cull Back). Плоскости сдвинуты по высоте: по оси ствола под кроной и через центр кроны выше, — пересекаются там же,
    где ствол и крона (нет «второго ствола» у наклонных и смещённых крон); 2 ряда (ствол, крона), ширина по силуэту.
    Шапка — горизонтальный квад с видом сверху на высоте самой широкой части кроны. 18 треугольников."""
    mesh = PMesh()
    H = tree.H
    S, Sw, xc, yc = bake['S'], bake['Sw'], bake['xc'], bake['yc']
    alpha = bake['alpha']
    cw, ch = CROSS_CELL
    cc = crown_centre(tree)
    top = S / 1.02
    z1 = lerp(tree.zcb, top, 0.35)     # стык рядов внутри нижней трети кроны: там его закрывает хвоя

    def off(pl, z, row):
        """Смещение плоскости pl: в ряду ствола — по оси ствола (с наклоном), в ряду кроны — через центр кроны
        (плоскость вертикальна: ребром не видна полосой)."""
        if row == 0:
            t = tree.center(z)
            return t.y if pl == 0 else t.x
        return cc.y if pl == 0 else cc.x

    def extent(k, z0, z1_):
        sub = alpha[:, k * cw:(k + 1) * cw]
        r0 = int((1 - z1_ / S) * ch)
        r1 = int(math.ceil((1 - z0 / S) * ch))
        band = sub[max(0, r0 - 2):min(ch, r1 + 2)] > 0.05
        cols = np.where(band.any(axis=0))[0]
        if len(cols) == 0:
            return -0.3, 0.3
        return ((cols.min() - 1) / cw - 0.5) * Sw, ((cols.max() + 2) / cw - 0.5) * Sw

    rows = [0.0, z1, top]
    for k in range(4):
        a = k * math.pi / 2
        right = Vector((math.cos(a), math.sin(a), 0))
        facing = Vector((math.sin(a), -math.cos(a), 0))
        pl = 0 if k in (0, 2) else 1
        c_h = xc * right.x + yc * right.y      # центр кадра по оси «вправо»
        for j in range(2):
            za, zb = rows[j], rows[j + 1]
            h0, h1 = extent(k, za, zb)
            quad = []
            for (hh, zz) in ((h0, za), (h1, za), (h1, zb), (h0, zb)):
                wpos = right * (c_h + hh)
                o = off(pl, zz, j)
                if pl == 0:
                    q = Vector((wpos.x, o, zz))
                else:
                    q = Vector((o, wpos.y, zz))
                # две стороны плоскости разнесены на 2 мм: совпадающие грани (лицевая/изнанка) не мерцают
                quad.append((q + facing * 0.001, hh))
            uvs, nrm, col = [], [], []
            for q, hh in quad:
                uvs.append(((k * cw + (hh / Sw + 0.5) * cw) / 2048.0, q.z / S))
                hz = q.z / max(H, 1)
                radial = right * (1 if hh > 0 else -1) * (0.25 if abs(hh) > 0.3 else 0.0)
                nrm.append((facing * 0.6 + radial + UP * (0.2 + 0.4 * hz)).normalized())
                wv = 0.0 if q.z < tree.zcb else 0.22 * ((q.z - tree.zcb) / max(H - tree.zcb, 1)) ** 1.5
                col.append((wv, wv, wv, wv))
            idx = [mesh.vert(q) for q, _ in quad]
            mesh.face(idx, M_LEAVES, uvs, col, nrm, [(0, q.z / H) for q, _ in quad])
    # шапка
    cx, cy, size = bake['top_box']
    zc = lerp(tree.zcb, top, 0.62)
    corners = [(-1, -1), (1, -1), (1, 1), (-1, 1)]
    idx, uvs, nrm, col = [], [], [], []
    wv = 0.22 * ((zc - tree.zcb) / max(H - tree.zcb, 1)) ** 1.5
    for sx, sy in corners:
        q = Vector((cx + sx * size / 2, cy + sy * size / 2, zc))
        idx.append(mesh.vert(q))
        uvs.append(((4 * cw + (sx * 0.5 + 0.5) * 512) / 2048.0, sy * 0.5 + 0.5))
        nrm.append((UP + Vector((sx, sy, 0)) * 0.15).normalized())
        col.append((wv, wv, wv, wv))
    mesh.face(idx, M_LEAVES, uvs, col, nrm, [(0, zc / H)] * 4)
    return mesh


def light_scene(scene):
    """Солнце + небо как в превью (для подгонки цвета креста под LOD2)."""
    world = bpy.data.worlds.new('calib_sky')
    scene.world = world
    nt = world.node_tree
    bg = nt.nodes['Background']
    bg.inputs['Color'].default_value = (0.5, 0.6, 0.75, 1)
    bg.inputs['Strength'].default_value = 0.55
    sun = bpy.data.lights.new('calib_sun', 'SUN')
    sun.energy = 1.9
    sun.angle = math.radians(0.6)
    so = bpy.data.objects.new('calib_sun', sun)
    eg.link(so)
    d = Vector((math.cos(math.radians(-60)) * math.cos(math.radians(48)),
                math.sin(math.radians(-60)) * math.cos(math.radians(48)), math.sin(math.radians(48))))
    so.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
    return so


def lit_crown_colour(scene, obj, tree, cams):
    """Средний цвет кроны (пиксели с альфой > 0.5 выше низа кроны) по нескольким камерам, без теней."""
    vals = []
    for loc, tgt in cams:
        cam = bpy.data.objects.new('ccam', bpy.data.cameras.new('ccam'))
        eg.link(cam)
        cam.data.lens = 50
        cam.location = loc
        cam.rotation_euler = (Vector(tgt) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
        scene.camera = cam
        path = os.path.join(bpy.app.tempdir or '.', 'calib.png')
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        px = eg.srgb_png_to_linear_values(path)
        m = (px[..., 3] > 0.5) & (px[..., 1] > px[..., 0] * 1.05)   # только хвоя (зелёные пиксели), без ствола
        if m.sum() > 50:
            vals.append(px[m][:, :3].mean(0))
        bpy.data.objects.remove(cam)
    return np.mean(vals, 0) if vals else np.ones(3)


def silhouette_cover(scene, obj, pass_mats, S, Sw, xc, yc, top_box, tmp, tag):
    """Непрозрачная площадь силуэта (м²) по 4 видам сбоку и сверху: белая эмиссия с отсечением альфы."""
    for s, m in zip(obj.material_slots, pass_mats):
        s.material = m
    v = render_views(scene, [obj], pass_mats, S, Sw, xc, yc, top_box, tmp, tag, passes=(3,), res=(192, 256),
                     top_res=256, samples=4)
    side = np.mean([(v[k][0][..., 3] > 0.5).mean() for k in range(4)]) * S * Sw
    top = (v[4][0][..., 3] > 0.5).mean() * top_box[2] ** 2
    return float(side), float(top)


# ------------------------------------------------------------------ сборка ассета

def make_textures(tex_dir):
    import pine_textures as pt
    import pine_needles as pn
    os.makedirs(tex_dir, exist_ok=True)
    eg.reset_scene()
    pt.bark_lower(tex_dir)
    pt.bark_upper(tex_dir)
    pn.needle_atlas(tex_dir, 'mature', seed=7)
    pn.needle_atlas(tex_dir, 'young', seed=11)


def load_atlas(tex_dir, needles):
    with open(os.path.join(tex_dir, needles + '_sprites.json')) as f:
        atlas = json.load(f)
    # центр непрозрачного каждого спрайта (для LOD-карточек) — по альфе атласа
    img = bpy.data.images.load(os.path.join(tex_dir, needles + '_albedo.png'), check_existing=False)
    w, h = img.size
    a = np.array(img.pixels[:], np.float32).reshape(h, w, 4)[..., 3]   # строки снизу вверх
    bpy.data.images.remove(img)
    for name, spr in atlas.items():
        u0, v0, u1, v1 = spr['uv']
        sub = a[int(v0 * h):int(v1 * h), int(u0 * w):int(u1 * w)] > 0.5
        ys, xs = np.nonzero(sub)
        if len(xs):
            spr['centroid'] = [float(xs.mean() / sub.shape[1]), float(ys.mean() / sub.shape[0])]
    return atlas


def build_asset(name, out_root, tex_dir, report):
    P = PRESETS[name]
    needles = 'pine_needles_young' if P['kind'] == 'young' else 'pine_needles_mature'
    eg.reset_scene()
    atlas = load_atlas(tex_dir, needles)
    with open(os.path.join(tex_dir, 'pine_bark_lower.json')) as f:
        bark_info = json.load(f)
    tree = Tree(name, P, atlas, bark_info)
    folder = os.path.join(out_root, name)
    os.makedirs(folder, exist_ok=True)
    ids = ['bark_lower', 'bark_upper', needles]
    prev_mats = [bark_material(ids[0], os.path.join(tex_dir, 'pine_bark_lower_albedo.png'),
                               os.path.join(tex_dir, 'pine_bark_lower_normal.png')),
                 bark_material(ids[1], os.path.join(tex_dir, 'pine_bark_upper_albedo.png'),
                               os.path.join(tex_dir, 'pine_bark_upper_normal.png')),
                 card_material(ids[2], os.path.join(tex_dir, needles + '_albedo.png'), 0.5)]
    pass_mats = [pass_material('pass_bark_lower', os.path.join(tex_dir, 'pine_bark_lower_albedo.png')),
                 pass_material('pass_bark_upper', os.path.join(tex_dir, 'pine_bark_upper_albedo.png')),
                 pass_material('pass_needles', os.path.join(tex_dir, needles + '_albedo.png'), 0.5)]
    info = dict(name=name, lods=[])
    # жёсткий предел LOD0: 10 тыс. треугольников — при превышении прорежаем вееры (площадь хвои сохраняется)
    for _ in range(14):
        test, _bt = build_lod(tree, 0)
        if test.tris <= 9900:
            break
        thin_sprays(tree, 0.94)
    tmp = os.path.join(folder, '_sil_tmp')
    os.makedirs(tmp, exist_ok=True)
    scene = bpy.context.scene
    card_scene(scene, 4)
    # LOD0
    m0, bt0 = build_lod(tree, 0)
    used0 = [m for m in (M_LOWER, M_UPPER, M_LEAVES) if any(f[1] == m for f in m0.f)]
    o0 = m0.to_object(f'{name}_LOD0', prev_mats, used0)
    add_nrm_attr(o0)
    vs0 = np.array([v.co[:] for v in o0.data.vertices])
    S, Sw, xc, yc = ortho_views(vs0)
    crown = vs0[vs0[:, 2] > tree.zcb - 0.5]
    tsize = max(np.ptp(crown[:, 0]), np.ptp(crown[:, 1])) * 1.1
    top_box = (0.5 * (crown[:, 0].min() + crown[:, 0].max()), 0.5 * (crown[:, 1].min() + crown[:, 1].max()), tsize)
    pm0 = [pass_mats[m] for m in used0]
    sil0 = silhouette_cover(scene, o0, pm0, S, Sw, xc, yc, top_box, tmp, 'l0')
    for s, m in zip(o0.material_slots, [prev_mats[m] for m in used0]):
        s.material = m
    objs = [o0]
    meshes = [m0]
    lods = [(f'{name}_LOD0.fbx', LOD_SCREEN[0], [ids[m] for m in used0], True)]
    info['lods'].append(dict(lod=0, tris=m0.tris, bark=bt0, cards=m0.tris - bt0, sil=sil0,
                             cards_orient=card_orientation(m0)))
    o0.hide_render = True
    # LOD1, LOD2: площадь карточек подгоняется под силуэт LOD0 (сбоку и сверху)
    for lod in (1, 2):
        k = 0.8 if lod == 1 else 0.7
        best = None
        for it in range(4):
            mesh, bt = build_lod(tree, lod, area_k=k)
            used = [m for m in (M_LOWER, M_UPPER, M_LEAVES) if any(f[1] == m for f in mesh.f)]
            ob = mesh.to_object(f'_tmp{lod}_{it}', prev_mats, used)
            add_nrm_attr(ob)
            sil = silhouette_cover(scene, ob, [pass_mats[m] for m in used], S, Sw, xc, yc, top_box, tmp, f'l{lod}{it}')
            ratio = 0.5 * (sil0[0] / max(sil[0], 1e-3) + sil0[1] / max(sil[1], 1e-3))
            err = abs(ratio - 1)
            bpy.data.objects.remove(ob)
            if best is None or err < best[0]:
                best = (err, k, sil)
            if err < 0.04:
                break
            k = clamp(k * ratio ** 1.3, 0.25, 2.5)
        k = best[1]
        mesh, bt = build_lod(tree, lod, area_k=k)
        used = [m for m in (M_LOWER, M_UPPER, M_LEAVES) if any(f[1] == m for f in mesh.f)]
        ob = mesh.to_object(f'{name}_LOD{lod}', prev_mats, used)
        objs.append(ob)
        meshes.append(mesh)
        lods.append((f'{name}_LOD{lod}.fbx', LOD_SCREEN[lod], [ids[m] for m in used], True))
        info['lods'].append(dict(lod=lod, tris=mesh.tris, bark=bt, cards=mesh.tris - bt, area_k=round(k, 3),
                                 sil=best[2], cards_orient=card_orientation(mesh)))
        ob.hide_render = True
    shutil.rmtree(tmp, ignore_errors=True)
    # крест из LOD0
    o0.hide_render = False
    bake = bake_cross(tree, o0, pm0, folder, name)
    for s, m in zip(o0.material_slots, [prev_mats[m] for m in used0]):
        s.material = m
    cmesh = cross_mesh(tree, bake)
    cross_names = save_cross(bake, folder, name)
    cross_id = name + '_cross'
    cross_prev = card_material(cross_id, os.path.join(folder, cross_names[0]), 0.5,
                               normal=os.path.join(folder, cross_names[1]), cull_back=True)
    cobj = cmesh.to_object(f'{name}_LOD3', [None, None, cross_prev], [M_LEAVES])
    # цвет креста под LOD2: оба без теней, солнце + небо, 3 камеры
    o0.hide_render = True
    lod2 = objs[2]
    add_nrm_attr(lod2)
    sc = bpy.context.scene
    sc.render.film_transparent = True
    sc.cycles.samples = 24
    sc.render.resolution_x, sc.render.resolution_y = 320, 320
    sun = light_scene(sc)
    for o in (lod2, cobj):
        o.visible_shadow = False
    Ht = tree.H
    cams = [((0, -Ht * 2.6, Ht * 0.55), (0, 0, Ht * 0.75)), ((Ht * 2.6, 0, Ht * 0.6), (0, 0, Ht * 0.75)),
            ((0, -Ht * 1.6, Ht * 2.0), (0, 0, Ht * 0.7))]
    gains = []
    for it in range(4):
        cobj.hide_render, lod2.hide_render = True, False
        c2 = lit_crown_colour(sc, lod2, tree, cams)
        cobj.hide_render, lod2.hide_render = False, True
        cx = lit_crown_colour(sc, cobj, tree, cams)
        g = np.clip(c2 / np.maximum(cx, 1e-4), 0.7, 1.4)
        gains.append([round(float(x), 3) for x in g])
        if np.all(np.abs(g - 1) < 0.03) or it == 3:
            break
        bake['albedo'][..., :3] = np.clip(bake['albedo'][..., :3] * g ** 1.6, 0, 1)
        save_cross(bake, folder, name)
        for im in bpy.data.images:
            if im.filepath and os.path.basename(im.filepath) == cross_names[0]:
                im.reload()
    info['cross_gain'] = gains
    for o in (lod2, cobj):
        o.visible_shadow = True
    bpy.data.objects.remove(sun)
    remove_nrm_attr(o0)
    remove_nrm_attr(lod2)
    objs.append(cobj)
    lods.append((f'{name}_LOD3.fbx', LOD_SCREEN[3], [cross_id], False))
    info['lods'].append(dict(lod=3, tris=cmesh.tris))
    for o in objs:
        o.hide_render = False
    for o, (fbx, _, _, _) in zip(objs, lods):
        eg.export_fbx([o], os.path.join(folder, fbx))
    for f in ('pine_bark_lower', 'pine_bark_upper', needles):
        for suf in ('_albedo.png', '_normal.png', '_mask.png'):
            shutil.copy(os.path.join(tex_dir, f + suf), os.path.join(folder, f + suf))
    height = round(float(vs0[:, 2].max()), 2)
    tops = [round(float(max(v[2] for v in m.v)), 2) for m in meshes] + [round(float(max(v[2] for v in cmesh.v)), 2)]
    materials = [
        dict(id='bark_lower', role='bark', albedo='pine_bark_lower_albedo.png', normal='pine_bark_lower_normal.png',
             mask='pine_bark_lower_mask.png', tiling=[1, 1], smoothnessMax=1.0, aoMax=1.0, normalScale=1.0, **WIND),
        dict(id='bark_upper', role='bark', albedo='pine_bark_upper_albedo.png', normal='pine_bark_upper_normal.png',
             mask='pine_bark_upper_mask.png', tiling=[1, 1], smoothnessMax=1.0, aoMax=1.0, normalScale=1.0, **WIND),
        dict(id=needles, role='leaves', albedo=needles + '_albedo.png', normal=needles + '_normal.png',
             mask=needles + '_mask.png', alphaCutoff=0.5, smoothnessMax=0.3, aoMax=1.0, normalScale=1.0,
             puddleSink=0.0, rotorWashClear=0.0, shiverDrag=0.04, healthyColor=[1.0, 1.0, 1.0],
             dryColor=[0.88, 0.9, 0.82], **WIND),
        dict(id=cross_id, role='cross', albedo=cross_names[0], normal=cross_names[1], alphaCutoff=0.5,
             healthyColor=[1.0, 1.0, 1.0], dryColor=[0.88, 0.9, 0.82], scaleFadeLength=400.0, shiverDrag=0.04,
             **WIND),
    ]
    trunk_r = tree.radius(1.3)
    data = eg.write_manifest(folder, name, 'tree', 'Pine',
                             [dict(fbx=f, screen=s, submeshes=i, castShadows=c) for f, s, i, c in lods],
                             materials, height=height, trunk_radius=round(trunk_r, 3), collider_height=height)
    info.update(height=height, lod_tops=tops, crown_base=round(tree.zcb, 2), dbh=tree.dbh,
                primaries=sum(1 for b in tree.branches if b.alive), sprays=len(all_sprays(tree)),
                parts={str(k): v for k, v in tree.parts.items()})
    report.append(info)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out_root, '_work', name + '.blend'), check_existing=False)
    return data


def main():
    args = eg.script_args()
    out_root = args[0]
    names = [a for a in args[1:] if not a.startswith('--')] or list(PRESETS)
    tex_dir = os.path.join(out_root, '_tex')
    os.makedirs(os.path.join(out_root, '_work'), exist_ok=True)
    if '--textures' in args or not os.path.exists(os.path.join(tex_dir, 'pine_needles_mature_sprites.json')):
        make_textures(tex_dir)
    report = []
    for n in names:
        build_asset(n, out_root, tex_dir, report)
        print('ASSET', json.dumps(report[-1]))
    with open(os.path.join(out_root, '_work', 'report_' + '_'.join(n[-2:] for n in names) + '.json'), 'w') as f:
        json.dump(report, f, indent=1)


if __name__ == '__main__':
    main()
