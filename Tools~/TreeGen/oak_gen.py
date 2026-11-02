# Дуб черешчатый (Quercus robur) лесостепи под Харьковом: генератор деревьев для Unity (Nature Renderer, LODGroup
# CrossFade), версия 2.
# Запуск (фоном):
#   "C:/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b --factory-startup --python oak_gen.py -- <папка> [имена...] [--textures]
#   имена — из PRESETS (по умолчанию все). Текстуры (oak_textures) — в <папка>/_tex, --textures — пересоздать.
#
# Устройство:
#  - крона — объединение эллипсоидов-«подкрон»: у лесного дуба 2-3 сводных лидера из развилки на 0.6-0.7 H, каждый с
#    верхней подкроной, ниже — 3-5 скелетных ветвей из ствола к подкронам на краю кроны (самая широкая часть — на
#    1/3-1/2 длины кроны сверху), из лидеров — боковые ветви; у раскидистого — 5-6 толстых ветвей из 2.5-6 м на разной
#    высоте, почти горизонтальных: провис, потом конец вверх (локоть), на каждой — внешняя подкрона и средняя на
#    восходящей ветви; сверху 1-2 подкроны. Между подкронами — тёмные провалы;
#  - скелетные ветви «локтями»: прямые отрезки 1-2.5 м с изломами 10-25° попеременно (зигзаг), ветви (L2) — отрезки
#    0.4-1.1 м с изломами 15-30°, до поверхности своей подкроны; длинные ветвятся ещё раз; непокрытые места оболочки
#    добираются ветвями от ближайшей; толщины — трубочная модель (сумма r^2.15), у основания ветвей — наплыв;
#  - листва LOD0 — сложенные по оси карточки-ветки атласа oak_leaves (4 треугольника) в узлах внешней части ветвей,
#    веером по 1-3 (псевдомутовки дуба), только в оболочке подкрон (до 2 / 2.5 м вглубь), лицом наружу-вверх (нормаль
#    листа ~ нормаль подкроны + вверх, внизу кроны — «зонтиком»), нормали вершин — к подкроне (бугры освещены каждый
#    по-своему); у земли (ниже 4.5 м) — мелкие ветки на коре, ничего не висит в воздухе;
#  - LOD1 — те же карточки плоскими (2 треугольника), меньше граней коры; LOD2 — карточки «подкрон» oak_clumps
#    (кластеры карточек LOD0), их размер подогнан по покрытию к LOD1; LOD3 — крест 24 треугольника (две вертикальные
#    плоскости с двумя сторонами и горизонтальная сверху), атлас 2048x512 из LOD1: альбедо x AO (самозатенение кроны
#    вдали, где Nature Renderer не рисует теней), нормали — гладкой оболочки подкрон (без мелкой мозаики);
#  - цвет вершин: альфа — вес ветра (0 у комля, растёт по ветвям; у карточек — от веса точки крепления к краю), у коры
#    RGB = 0, у листвы RGB = A; UV1 — (0, z/H);
#  - сухостой (dead=True; дизайн леса, этап 3): скелет растёт как у живого (толщины — по живой кроне), потом умирает
#    (OakSkeleton.die): без листвы, тонкие веточки (branch2) большей частью отвалились, из веток L2 отвалилась седьмая
#    часть и ещё треть обломана, треть скелетных ветвей обломана (рваный слом; голый «мультяшный» скелет без веток
#    читался рисунком); снаг (snag=высота, м) — ствол обломан на этой высоте с рваным верхом,
#    от ветвей — короткие обломки. Кора — серая выветренная (oak_dead_bark — фото-скан, oak_dead_branch — кора ветвей
#    без цвета; готовит их photo_bark.py / dead_branch_texture в _tex); крест — с голого скелета;
#  - бурелом (broken_at=высота, м): мёртвое дерево переломлено на этой высоте — рваный пень стоит, верхняя часть (бревно
#    'log' с кроной) упала рядом: повёрнута вокруг места слома на ~90° в сторону падения и опущена к земле обломанным
#    концом в ~0.6 м от пня; ветви, что ушли бы в землю, обломаны о неё (там, где пересекли её), упавшее не качается.
import json
import math
import os
import random
import shutil
import sys
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bmesh  # noqa: E402
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Matrix, Quaternion, Vector, noise  # noqa: E402

import envgen_common as eg  # noqa: E402
import oak_textures as ot  # noqa: E402

UP = Vector((0.0, 0.0, 1.0))
GROUP = 'Oak'
LOD_SCREEN = (0.45, 0.18, 0.06, 0.004)
LOW_Z = 4.5          # м: ниже — листва в досягаемости камеры дрона на взлёте
BARK_MIN_R = 0.055   # м: ветви толще (у основания) — в коре ствола (бороздчатой), тоньше — в гладкой коре ветвей
SHEAR = 0.21         # сдвиг U коры ствола на плитку по высоте: борозды чуть винтом, повтор не встаёт столбиком
PIPE_N = 2.15

PRESETS = {
    # лесные: прямой ствол, чистый до 42-48 % высоты, развилка на 2-3 сводных лидера, неровная высокая крона
    'oak_forest_01': dict(form='forest', H=23.0, dbh=0.46, bole=0.44, fork=0.63, crown_w=11.0, leaders=3, low_limbs=4,
                          asym=0.15, budget=10500, seed_offset=0),
    'oak_forest_02': dict(form='forest', H=25.0, dbh=0.52, bole=0.47, fork=0.67, crown_w=12.5, leaders=2, low_limbs=5,
                          asym=0.38, flat_top=True, budget=10500, seed_offset=0),
    'oak_forest_03': dict(form='forest', H=21.0, dbh=0.40, bole=0.42, fork=0.66, crown_w=9.5, leaders=2, low_limbs=3,
                          asym=0.22, dead_top=3, budget=10000, seed_offset=0),
    # раскидистые (опушка, село): короткий толстый ствол, крона шире высоты
    'oak_open_01': dict(form='open', H=15.0, dbh=0.95, bole=3.0, crown_w=19.0, limbs=5, budget=11800, seed_offset=0),
    'oak_open_02': dict(form='open', H=17.0, dbh=1.10, bole=3.6, crown_w=20.5, limbs=6, broken=1, budget=11800,
                        seed_offset=0),
    # сухостой: мёртвый лесной дуб и снаги (обломанные стволы)
    'oak_dead_01': dict(form='forest', H=20.0, dbh=0.42, bole=0.45, fork=0.65, crown_w=10.0, leaders=2, low_limbs=4,
                        asym=0.25, dead=True, budget=9000, seed_offset=7),
    'oak_dead_02': dict(form='forest', H=16.0, dbh=0.34, bole=0.5, fork=0.68, crown_w=8.0, leaders=2, low_limbs=3,
                        asym=0.35, dead=True, budget=8000, seed_offset=19),
    'oak_snag_01': dict(form='forest', H=21.0, dbh=0.46, bole=0.45, fork=0.66, crown_w=10.0, leaders=2, low_limbs=3,
                        asym=0.2, dead=True, snag=7.5, budget=4000, seed_offset=11),
    'oak_snag_02': dict(form='forest', H=18.0, dbh=0.36, bole=0.45, fork=0.66, crown_w=9.0, leaders=2, low_limbs=3,
                        asym=0.2, dead=True, snag=4.2, budget=3000, seed_offset=13),
    # бурелом: переломленный ствол — пень стоит, вершина с кроной лежит
    'oak_broken_01': dict(form='forest', H=20.0, dbh=0.42, bole=0.45, fork=0.65, crown_w=10.0, leaders=2, low_limbs=4,
                          asym=0.25, dead=True, broken_at=2.8, fall=88.0, budget=9000, seed_offset=23),
    'oak_broken_02': dict(form='forest', H=18.0, dbh=0.38, bole=0.47, fork=0.66, crown_w=9.0, leaders=2, low_limbs=3,
                          asym=0.3, dead=True, broken_at=0.9, fall=90.0, budget=8000, seed_offset=29),
    'oak_broken_03': dict(form='forest', H=23.0, dbh=0.48, bole=0.44, fork=0.63, crown_w=11.0, leaders=3, low_limbs=4,
                          asym=0.2, dead=True, broken_at=4.6, fall=86.0, budget=10000, seed_offset=31),
}
DEAD_IDS = ['oak_dead_bark', 'oak_dead_branch']


def rot(v, axis, ang):
    v = Vector(v)
    a = Vector(axis)
    if a.length < 1e-9:
        return v
    v.rotate(Quaternion(a.normalized(), ang))
    return v


def perp_axis(d, rng):
    a = Vector(d).orthogonal().normalized()
    return rot(a, d, rng.uniform(0, math.tau))


def horiz(v):
    h = Vector((v.x, v.y, 0.0))
    return h.normalized() if h.length > 1e-6 else Vector((1.0, 0.0, 0.0))


# ------------------------------------------------------------------ крона: объединение эллипсоидов

class Crown:
    def __init__(self):
        self.cl, self.rl, self.owner = [], [], []
        self.c = self.r = None

    def add(self, c, r, owner=None):
        self.cl.append(Vector(c))
        self.rl.append(tuple(r))
        self.owner.append(owner)
        self.c = np.array([tuple(x) for x in self.cl], np.float64)
        self.r = np.array(self.rl, np.float64)
        return len(self.cl) - 1

    def scale(self, i, k):
        self.rl[i] = tuple(x * k for x in self.rl[i])
        self.r = np.array(self.rl, np.float64)

    def f(self, P):
        P = np.atleast_2d(np.asarray(P, np.float64))
        return np.sqrt((((P[:, None, :] - self.c[None]) / self.r[None]) ** 2).sum(-1))

    def fmin(self, p):
        f = self.f([tuple(p)])[0]
        i = int(np.argmin(f))
        return float(f[i]), i

    def inside(self, p):
        return self.fmin(p)[0] < 1.0

    def depth(self, p):
        """Глубина под поверхностью (м, приблизительно; < 0 — снаружи) и номер подкроны."""
        f, i = self.fmin(p)
        return (1.0 - f) * float(self.r[i].mean()), i

    def normal(self, p, i=None):
        if i is None:
            i = self.fmin(p)[1]
        g = (np.asarray(tuple(p)) - self.c[i]) / self.r[i] ** 2
        n = Vector(tuple(g))
        return n.normalized() if n.length > 1e-9 else UP.copy()

    def exit_dist(self, p, d, tmax=9.0, step=0.12):
        """Сколько пройти от p по d до выхода из кроны; снаружи — сначала вход (до 1.5 м)."""
        t = np.arange(0.0, tmax, step)
        P = np.asarray(tuple(p))[None] + t[:, None] * np.asarray(tuple(d))[None]
        ins = self.f(P).min(1) < 1.0
        if not ins[0]:
            first = np.where(ins)[0]
            if not len(first) or t[first[0]] > 1.5:
                return None
            k0 = first[0]
        else:
            k0 = 0
        out = np.where(~ins[k0:])[0]
        return float(t[k0 + out[0]]) if len(out) else float(tmax)

    def surface_points(self, spacing, rng):
        """Точки открытой поверхности объединения (не внутри соседних подкрон): [(точка, номер, нормаль)]."""
        res = []
        golden = math.pi * (3 - math.sqrt(5))
        for i, (c, r) in enumerate(zip(self.cl, self.rl)):
            a, b, cc = r
            p_ = 1.6
            area = 4 * math.pi * (((a * b) ** p_ + (a * cc) ** p_ + (b * cc) ** p_) / 3) ** (1 / p_)
            n = max(12, int(area / spacing ** 2))
            ph0 = rng.uniform(0, math.tau)
            for k in range(n):
                z = 1 - 2 * (k + 0.5) / n
                rr = math.sqrt(max(0.0, 1 - z * z))
                ph = k * golden + ph0
                u = Vector((math.cos(ph) * rr, math.sin(ph) * rr, z))
                q = c + Vector((u.x * a, u.y * b, u.z * cc))
                f = self.f([tuple(q)])[0]
                f[i] = 9.0
                if f.min() < 1.0:
                    continue
                res.append((q, i, self.normal(q, i), u.z))
        return res

    def extents(self, rng, n=4000):
        pts = []
        for p, i, nrm, uz in self.surface_points(0.5, rng):
            pts.append(tuple(p))
        P = np.array(pts)
        return P.min(0), P.max(0)


# ------------------------------------------------------------------ скелет

class Br:
    def __init__(self, level, kind, pts, parent=None, t_par=0.0):
        self.level = level
        self.kind = kind
        self.pts = [Vector(p) for p in pts]
        self.parent = parent
        self.t_par = t_par
        self.children = []
        self.radii = []
        self.w = []
        self.kinks = set()
        self.dead = False
        self.mat = 'bark'
        self.sub = None
        self.cap = 0.0
        self.collar = 0.0
        self._cum = None
        if parent is not None:
            parent.children.append(self)

    @property
    def cum(self):
        if self._cum is None or len(self._cum) != len(self.pts):
            c = [0.0]
            for i in range(1, len(self.pts)):
                c.append(c[-1] + (self.pts[i] - self.pts[i - 1]).length)
            self._cum = c
        return self._cum

    @property
    def length(self):
        return self.cum[-1]

    def at(self, t):
        L = self.length
        s = min(max(t, 0.0), 1.0) * L
        c = self.cum
        for i in range(len(self.pts) - 1):
            if c[i + 1] >= s or i == len(self.pts) - 2:
                seg = max(c[i + 1] - c[i], 1e-9)
                f = min(max((s - c[i]) / seg, 0.0), 1.0)
                p = self.pts[i].lerp(self.pts[i + 1], f)
                d = (self.pts[i + 1] - self.pts[i]).normalized()
                r = (self.radii[i] + (self.radii[i + 1] - self.radii[i]) * f) if self.radii else 0.0
                w = (self.w[i] + (self.w[i + 1] - self.w[i]) * f) if self.w else 0.0
                return p, d, r, w
        return self.pts[-1], (self.pts[-1] - self.pts[-2]).normalized(), 0.0, 0.0


def elbow_path(rng, start, d0, length, seg=(1.0, 2.0), kink=(10, 22), target=None, steer=0.3, up=0.0, sag=0.0,
               upturn=0.0, sub=2, curl=0.02, zmin_dir=None):
    """Ветвь «локтями»: прямые отрезки seg (м) с изломами kink (град) попеременно в стороны, на каждом — sub
    промежуточных узлов (лёгкий изгиб curl рад). steer — тяга к target, up — к свету, sag/upturn — провис в начале
    и подъём конца (раскидистые ветви)."""
    pts = [Vector(start)]
    d = Vector(d0).normalized()
    s = 0.0
    kinks = set()
    side = rng.choice([-1, 1])
    k_axis = perp_axis(d, rng)
    first = True
    while s < length - 1e-3:
        L = min(rng.uniform(*seg), length - s)
        if length - s - L < seg[0] * 0.45:
            L = length - s
        t = s / max(length, 1e-6)
        desired = d.copy()
        if target is not None and steer > 0:
            to = Vector(target) - pts[-1]
            if to.length > 1e-3:
                desired = desired * (1 - steer) + to.normalized() * steer
        desired = desired + UP * (up - sag * (1 - t) * 1.4 + upturn * max(0.0, t - 0.5) * 2.6)
        desired.normalize()
        if not first:
            k_axis = rot(k_axis, desired, rng.uniform(-0.8, 0.8))
            k_axis = (k_axis - desired * k_axis.dot(desired))
            k_axis = k_axis.normalized() if k_axis.length > 1e-6 else perp_axis(desired, rng)
            nd = rot(desired, k_axis, side * math.radians(rng.uniform(*kink)))
            side = -side
            kinks.add(len(pts) - 1)
        else:
            nd = desired
            first = False
        if zmin_dir is not None and nd.z < zmin_dir:
            nd.z = zmin_dir
            nd.normalize()
        bend_ax = perp_axis(nd, rng)
        nsub = sub if L > 1.6 else 1
        for k in range(nsub):
            nd = rot(nd, bend_ax, rng.gauss(0, curl))
            pts.append(pts[-1] + nd * (L / nsub))
        d = nd
        s += L
    return pts, kinks


class OakSkeleton:
    def __init__(self, name, P):
        self.name = name
        self.P = P
        self.seed = (zlib.crc32(name.encode()) + P.get('seed_offset', 0)) & 0x7fffffff
        self.rng = random.Random(self.seed)
        self.H = P['H']
        self.branches = []
        self.crown = Crown()
        self.epicormic = []
        if P['form'] == 'forest':
            self.build_forest()
        else:
            self.build_open()
        self.grow_branches()
        self.fill_shell()
        self.add_stubs()
        self.compute_radii()
        self.compute_wind()
        self.assign_materials()
        if P.get('dead'):
            self.die()

    # --- сухостой: живой скелет теряет листву, тонкие веточки, часть ветвей (толщины остаются от живого дерева)
    def die(self):
        P = self.P
        rng = random.Random(self.seed + 77)
        snag = P.get('snag')
        # сами ветви, не id(): выброшенные объекты освобождаются, и новый (бревно бурелома) получал id выброшенного
        kill = set()

        def drop(b):
            kill.add(b)
            for c in b.children:
                drop(c)

        def cut(b, frac):
            # остаётся начало ветви до доли frac длины; дети дальше — прочь, ближе — t по новой длине
            s_cut = frac * b.length
            c = b.cum
            k = next((i for i in range(1, len(b.pts)) if c[i] >= s_cut), len(b.pts) - 1)
            f = (s_cut - c[k - 1]) / max(c[k] - c[k - 1], 1e-9)
            b.pts = b.pts[:k] + [b.pts[k - 1].lerp(b.pts[k], f)]
            b.radii = b.radii[:k] + [b.radii[k - 1] + (b.radii[k] - b.radii[k - 1]) * f]
            b.w = b.w[:k] + [b.w[k - 1] + (b.w[k] - b.w[k - 1]) * f]
            b._cum = None
            b.kinks = set(i for i in b.kinks if isinstance(i, int) and i < k) if isinstance(b.kinks, set) else b.kinks
            for ch in list(b.children):
                if ch.t_par > frac:
                    drop(ch)
                else:
                    ch.t_par = ch.t_par / max(frac, 1e-6)
            if b.kind != 'trunk':
                b.kind = 'broken'

        if snag:
            cut(self.trunk, self.t_at_z(self.trunk, snag))
            self.snag_top = True
        # тонкие веточки — большей частью отвалились, оставшиеся обломаны; ветки L2 — часть отвалилась, часть обломана
        for b in list(self.branches):
            if b in kill or b.kind != 'branch2':
                continue
            u = rng.random()
            if snag or u < 0.6:
                drop(b)
            elif u < 0.85:
                cut(b, rng.uniform(0.4, 0.8))
        for b in list(self.branches):
            if b in kill or b.kind != 'branch':
                continue
            u = rng.random()
            if u < (0.55 if snag else 0.15):
                drop(b)
            elif u < (0.9 if snag else 0.5):
                cut(b, rng.uniform(0.3, 0.75))
        for b in list(self.branches):
            if b in kill or b.kind not in ('limb', 'leader'):
                continue
            if snag:
                cut(b, rng.uniform(0.12, 0.35))
            elif rng.random() < 0.3:
                cut(b, rng.uniform(0.55, 0.9))
        self.branches = [b for b in self.branches if b not in kill]
        for b in self.branches:
            b.children = [c for c in b.children if c not in kill]
        if P.get('broken_at'):
            self.fall(P['broken_at'], P.get('fall', 88.0), rng, cut)
            self.branches = [b for b in self.branches if b not in kill]
            for b in self.branches:
                b.children = [c for c in b.children if c not in kill]

    # --- бурелом: слом ствола на высоте hb, верхняя часть с кроной падает рядом
    def fall(self, hb, angle_deg, rng, cut):
        trunk = self.trunk
        t = self.t_at_z(trunk, hb)
        c = trunk.cum
        s_cut = t * trunk.length
        k = next((i for i in range(1, len(trunk.pts)) if c[i] >= s_cut), len(trunk.pts) - 1)
        f = (s_cut - c[k - 1]) / max(c[k] - c[k - 1], 1e-9)
        p_break = trunk.pts[k - 1].lerp(trunk.pts[k], f)
        r_break = trunk.radii[k - 1] + (trunk.radii[k] - trunk.radii[k - 1]) * f
        w_break = trunk.w[k - 1] + (trunk.w[k] - trunk.w[k - 1]) * f
        # верхняя часть — бревно задом наперёд: тонкая вершина в начале, слом — в конце (там рваный торец)
        pts = [p_break] + trunk.pts[k:]
        radii = [r_break] + trunk.radii[k:]
        ws = [w_break] + trunk.w[k:]
        log = Br(0, 'log', list(reversed(pts)))
        log.radii = list(reversed(radii))
        log.w = list(reversed(ws))
        log.mat = 'bark'
        for ch in list(trunk.children):
            if ch.t_par > t:
                trunk.children.remove(ch)
                ch.parent = log
                ch.t_par = 1.0 - (ch.t_par - t) / max(1.0 - t, 1e-6)
                log.children.append(ch)
        cut(trunk, t)
        self.branches.append(log)
        self.snag_top = True
        # поворот вокруг места слома: вершина падает в сторону az
        az = rng.uniform(0, math.tau)
        direction = Vector((math.cos(az), math.sin(az), 0.0))
        axis = UP.cross(direction).normalized()
        q = Quaternion(axis, math.radians(angle_deg))
        drop = Vector((0, 0, -p_break.z + max(r_break, 0.1) + 0.02)) + direction * 0.6
        fallen = []

        def collect(b):
            fallen.append(b)
            for ch in b.children:
                collect(ch)

        collect(log)
        for b in fallen:
            b.pts = [q @ (p - p_break) + p_break + drop for p in b.pts]
            b._cum = None
            b.w = [w * 0.1 for w in b.w]
        # ветви, ушедшие в землю, обломаны о неё (где пересекли), упавшие целиком под землю — прочь
        ground = 0.03
        for b in fallen:
            if b is log or b.parent is None:
                continue
            zs = [p.z for p in b.pts]
            if zs[0] < ground:
                cut(b, 0.02)
                continue
            for i in range(1, len(zs)):
                if zs[i] < ground:
                    fz = (zs[i - 1] - ground) / max(zs[i - 1] - zs[i], 1e-6)
                    cc = b.cum
                    frac = (cc[i - 1] + (cc[i] - cc[i - 1]) * fz) / max(b.length, 1e-6)
                    cut(b, max(frac, 0.02))
                    break

    def add(self, br):
        self.branches.append(br)
        return br

    # --- ствол
    def make_trunk(self, z_top, lean_amt=0.012):
        rng = self.rng
        H = self.H
        lean = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0)).normalized() * lean_amt
        seed3 = Vector((rng.uniform(0, 100), rng.uniform(0, 100), rng.uniform(0, 100)))
        zs = [-0.4, 0.0, 0.15, 0.35, 0.6, 1.0, 1.3, 1.8]
        z = 1.8
        while z < z_top - 0.6:
            z += rng.uniform(1.0, 1.5)
            zs.append(min(z, z_top - 0.4))
        zs.append(z_top)
        pts = []
        for z in zs:
            wob = Vector((noise.noise(seed3 + Vector((z * 0.15, 0, 0))), noise.noise(seed3 + Vector((0, z * 0.15, 5))), 0))
            off = lean * max(z, 0) + wob * (0.012 * H) * min(1.0, max(z, 0) / 3.0)
            pts.append(Vector((off.x, off.y, z)))
        tr = self.add(Br(0, 'trunk', pts))
        return tr

    def t_at_z(self, br, z):
        best, bt = 1e9, 0.0
        for k in range(201):
            t = k / 200
            d = abs(br.at(t)[0].z - z)
            if d < best:
                best, bt = d, t
        return bt

    def limb(self, parent, t, d0, target, kind='limb', level=1, seg=(1.0, 2.0), kink=(12, 24), steer=0.3, up=0.03,
             sag=0.0, upturn=0.0, overshoot=1.0, sub=2):
        p0, dpar, _, _ = parent.at(t)
        L = (Vector(target) - p0).length * overshoot
        pts, kinks = elbow_path(self.rng, p0, d0, L, seg, kink, target, steer, up, sag, upturn, sub)
        br = self.add(Br(level, kind, pts, parent, t))
        br.kinks = kinks
        return br

    # --- лесная форма
    def build_forest(self):
        P, rng = self.P, self.rng
        H, W = P['H'], P['crown_w']
        R = W * 0.5
        zcb = P['bole'] * H
        zf = P['fork'] * H
        CL = H - zcb
        self.crown_base = zcb
        a_g = rng.uniform(0, math.tau)
        g = Vector((math.cos(a_g), math.sin(a_g), 0))
        asym = P.get('asym', 0.15)

        def shift(hdir, k=1.0):  # крона тянется к просвету g
            return g * (R * asym * k * (0.55 + 0.45 * hdir.dot(g)))

        trunk = self.make_trunk(zf)
        self.trunk = trunk
        F = trunk.pts[-1]
        flat = P.get('flat_top', False)
        n = P['leaders']
        a0 = rng.uniform(0, math.tau)
        self.leaders = []
        for i in range(n):
            a = a0 + i * math.tau / n + rng.uniform(-0.45, 0.45)
            hd = Vector((math.cos(a), math.sin(a), 0))
            rx = W * rng.uniform(0.26, 0.31) * (1.12 if flat else 1.0)
            rz = CL * rng.uniform(0.2, 0.25) * (0.78 if flat else 1.0)
            rho = R * rng.uniform(0.2, 0.36)
            cz = H - rz * (1.0 if i == 0 else rng.uniform(1.05, 1.35))
            c = hd * rho + shift(hd, 0.6) + Vector((0, 0, cz))
            tgt = c + UP * rz * 0.5 + hd * rx * 0.15
            lean = math.radians(rng.uniform(16, 30))
            d0 = UP * math.cos(lean) + hd * math.sin(lean)
            br = self.limb(trunk, 1.0, d0, tgt, 'leader', 1, seg=(1.3, 2.3), kink=(10, 20), steer=0.38, up=0.02)
            br.sub = self.crown.add(c, (rx, rx * rng.uniform(0.85, 1.0), rz), br)
            self.leaders.append(br)
        # скелетные ветви из ствола между низом кроны и развилкой
        nl = P['low_limbs']
        a1 = rng.uniform(0, math.tau)
        for k in range(nl):
            z = zcb + (zf - zcb) * (k + rng.uniform(0.0, 0.5)) / nl
            t = self.t_at_z(trunk, z)
            a = a1 + k * math.radians(137.5) + rng.uniform(-0.4, 0.4)
            hd = Vector((math.cos(a), math.sin(a), 0))
            rx = W * rng.uniform(0.17, 0.23)
            rz = CL * rng.uniform(0.16, 0.22)
            rho = R - rx * rng.uniform(0.65, 0.9)
            cz = max(zcb + CL * rng.uniform(0.2, 0.4), z + rz * 0.55)
            c = hd * rho + shift(hd) + Vector((0, 0, cz))
            low = (z - zcb) / max(zf - zcb, 1.0)
            ins = math.radians(rng.uniform(52, 66) * (1 - low) + rng.uniform(34, 46) * low)
            d0 = UP * math.cos(ins) + hd * math.sin(ins)
            tgt = c + hd * rx * 0.35 + UP * rz * 0.15
            br = self.limb(trunk, t, d0, tgt, 'limb', 1, seg=(1.0, 2.0), kink=(12, 25), steer=0.3, up=0.02,
                           upturn=0.05)
            br.sub = self.crown.add(c, (rx, rx * rng.uniform(0.8, 1.0), rz), br)
        # боковые ветви лидеров к краю кроны в верхней половине
        for L_ in self.leaders:
            for j in range(rng.choice([1, 2, 2])):
                t = rng.uniform(0.22, 0.55)
                p0, dl, _, _ = L_.at(t)
                hd = horiz(p0 - Vector((0, 0, p0.z))) if Vector((p0.x, p0.y, 0)).length > 0.3 else horiz(dl)
                hd = rot(hd, UP, rng.uniform(-1.0, 1.0))
                rx = W * rng.uniform(0.15, 0.21)
                rz = CL * rng.uniform(0.14, 0.19)
                rho = R - rx * rng.uniform(0.7, 0.95)
                cz = zcb + CL * rng.uniform(0.55, 0.78)
                c = hd * rho + shift(hd) + Vector((0, 0, cz))
                ins = math.radians(rng.uniform(32, 50))
                d0 = (dl * math.cos(ins) + hd * math.sin(ins)).normalized()
                tgt = c + hd * rx * 0.3
                br = self.limb(L_, t, d0, tgt, 'limb', 2, seg=(0.9, 1.8), kink=(12, 25), steer=0.32, up=0.03,
                               upturn=0.05)
                br.sub = self.crown.add(c, (rx, rx * rng.uniform(0.8, 1.0), rz), br)
        # суховершинность: 1-2 сухих сука над кроной у одного лидера
        self.dead_spikes = []
        nd = P.get('dead_top', 0)
        if nd:
            L_ = self.leaders[0]
            self.crown.scale(L_.sub, 0.82)
            for j in range(nd):
                t = rng.uniform(0.6, 0.9)
                p0, dl, _, _ = L_.at(t)
                d0 = (dl + perp_axis(dl, rng) * rng.uniform(0.2, 0.5) + UP * 1.1).normalized()
                Ls = rng.uniform(3.6, 5.0)
                pts, kinks = elbow_path(rng, p0, d0, Ls, (0.5, 1.0), (15, 32), None, 0, 0.15, sub=1)
                br = self.add(Br(2, 'dead', pts, L_, t))
                br.kinks = kinks
                br.dead = True
                self.dead_spikes.append(br)

    # --- раскидистая форма
    def build_open(self):
        P, rng = self.P, self.rng
        H, W = P['H'], P['crown_w']
        R = W * 0.5
        zb = P['bole']
        CLo = H - zb
        self.crown_base = zb
        stem_top = zb + min(CLo * rng.uniform(0.25, 0.32), 3.2)
        trunk = self.make_trunk(stem_top, 0.02)
        self.trunk = trunk
        n = P['limbs']
        a0 = rng.uniform(0, math.tau)
        self.massive = []
        zs = sorted([zb - 0.4 + (stem_top - zb - 0.2) * (k + rng.uniform(0, 0.6)) / n for k in range(n)])
        order = list(range(n))
        rng.shuffle(order)
        for k in range(n):
            z = zs[k]
            t = self.t_at_z(trunk, z)
            a = a0 + order[k] * math.tau / n + rng.uniform(-0.35, 0.35)
            hd = Vector((math.cos(a), math.sin(a), 0))
            low = (z - (zb - 0.4)) / max(stem_top - zb, 1.0)
            ins = math.radians(rng.uniform(74, 88) * (1 - low) + rng.uniform(48, 62) * low)
            rx = W * rng.uniform(0.2, 0.25)
            rz = CLo * rng.uniform(0.24, 0.3)
            rho_end = R - rx * rng.uniform(0.85, 1.05)
            z_end = z + rho_end * (rng.uniform(0.05, 0.2) * (1 - low) + rng.uniform(0.3, 0.5) * low)
            end = hd * rho_end + Vector((0, 0, z_end))
            d0 = UP * math.cos(ins) + hd * math.sin(ins)
            br = self.limb(trunk, t, d0, end, 'limb', 1, seg=(1.4, 2.4), kink=(14, 28), steer=0.2, up=0.0,
                           sag=0.12 * (1 - low), upturn=0.1, overshoot=1.08)
            e = br.pts[-1]
            c = e + UP * rz * 0.35 + hd * rx * 0.1
            c.z = max(c.z, 1.9 + rz)
            br.sub = self.crown.add(c, (rx, rx * rng.uniform(0.85, 1.0), rz), br)
            self.massive.append(br)
            # средняя подкрона на восходящей ветви из середины толстой
            if k % 2 == 1:
                continue
            t2 = rng.uniform(0.35, 0.55)
            p0, dl, _, _ = br.at(t2)
            rx2 = W * rng.uniform(0.21, 0.26)
            rz2 = CLo * rng.uniform(0.23, 0.29)
            hd2 = rot(hd, UP, rng.uniform(-0.5, 0.5))
            c2 = hd2 * (R * rng.uniform(0.45, 0.62)) + Vector((0, 0, zb + CLo * rng.uniform(0.5, 0.7)))
            d2 = (UP * 0.55 + hd2 * 0.85).normalized()
            b2 = self.limb(br, t2, d2, c2 + UP * rz2 * 0.2, 'limb', 2, seg=(1.0, 1.9), kink=(12, 24), steer=0.33,
                           up=0.02)
            b2.sub = self.crown.add(c2, (rx2, rx2 * rng.uniform(0.85, 1.0), rz2), b2)
        # верх: 1-2 подкроны над стволом-продолжением
        for j in range(rng.choice([1, 2])):
            a = rng.uniform(0, math.tau)
            hd = Vector((math.cos(a), math.sin(a), 0))
            rx = W * rng.uniform(0.21, 0.26)
            rz = CLo * rng.uniform(0.22, 0.28)
            c = hd * (R * rng.uniform(0.05, 0.25)) + Vector((0, 0, H - rz))
            d0 = (UP + hd * 0.35).normalized()
            br = self.limb(trunk, 1.0, d0, c + UP * rz * 0.4, 'leader', 1, seg=(1.2, 2.0), kink=(10, 22), steer=0.35,
                           up=0.02)
            br.sub = self.crown.add(c, (rx, rx * rng.uniform(0.85, 1.0), rz), br)
        # обломанная толстая ветвь (open_02)
        self.dead_spikes = []
        for j in range(P.get('broken', 0)):
            z = zb + rng.uniform(0.3, 1.2)
            t = self.t_at_z(trunk, z)
            hd = Vector((math.cos(a0 + 0.5), math.sin(a0 + 0.5), 0))
            pts, kinks = elbow_path(rng, trunk.at(t)[0], (UP * 0.45 + hd).normalized(), rng.uniform(1.6, 2.4), (0.8, 1.3),
                                    (8, 16), None, 0, 0.02, sub=1)
            br = self.add(Br(1, 'broken', pts, trunk, t))
            br.dead = True
            self.dead_spikes.append(br)

    # --- ветви L2
    def grow_branches(self):
        P, rng = self.P, self.rng
        forest = P['form'] == 'forest'
        cr = self.crown
        parents = [b for b in self.branches if b.kind in ('limb', 'leader')]
        target = P.get('n_branches', 80 if forest else 85)
        t_min = 0.3 if forest else 0.12
        elig = 0.0
        for b in parents:
            n = 12
            for k in range(n):
                t = t_min + (1 - t_min) * (k + 0.5) / n
                if cr.depth(b.at(t)[0])[0] > -1.2:
                    elig += b.length * (1 - t_min) / n
        spacing = min(max(elig / max(target / 1.35, 1), 0.45), 1.6)
        self.l2_spacing = spacing
        golden = math.radians(137.5)
        for par in parents:
            L = par.length
            s = t_min * L + rng.uniform(0, spacing)
            az = rng.uniform(0, math.tau)
            while s < L * 0.97:
                t = s / L
                p, d, _, _ = par.at(t)
                s += spacing * rng.uniform(0.75, 1.25)
                dep, si = cr.depth(p)
                if dep < -1.2:
                    continue
                az += golden + rng.uniform(-0.5, 0.5)
                ax = rot(d.orthogonal().normalized(), d, az)
                ins = math.radians(rng.uniform(40, 70))
                dd = rot(d, ax, ins)
                out = horiz(p) if Vector((p.x, p.y, 0)).length > 0.2 else horiz(dd)
                dd = (dd + UP * (0.18 if forest else 0.2) + out * 0.3).normalized()
                zlim = -0.3 if forest else -0.55
                if dd.z < zlim:
                    dd = rot(d, ax, -ins)
                    dd = (dd + UP * 0.35 + out * 0.28).normalized()
                    if dd.z < zlim:
                        continue
                ex = cr.exit_dist(p, dd)
                if ex is None or ex < 0.6:
                    continue
                Lb = min(max(ex * rng.uniform(0.85, 1.0), 0.6), 4.5 if forest else 5.5)
                pts, kinks = elbow_path(rng, p, dd, Lb, (0.55, 1.2) if forest else (0.8, 1.6), (15, 30), None, 0, 0.1, sub=1, curl=0.03,
                                        zmin_dir=zlim)
                br = self.add(Br(par.level + 1, 'branch', pts, par, t))
                br.kinks = kinks
                if Lb > 2.5:
                    for j in range(rng.choice([0, 1, 1, 2] if forest else [0, 1, 1])):
                        t2 = rng.uniform(0.35, 0.85)
                        p2, d2, _, _ = br.at(t2)
                        ax2 = perp_axis(d2, rng)
                        dd2 = rot(d2, ax2, math.radians(rng.uniform(45, 75)))
                        dd2 = (dd2 + UP * 0.25 + out * 0.2).normalized()
                        if dd2.z < zlim:
                            dd2.z = zlim
                            dd2.normalize()
                        ex2 = cr.exit_dist(p2, dd2, tmax=4.0)
                        if ex2 is None or ex2 < 0.45:
                            continue
                        L2 = min(max(ex2 * rng.uniform(0.75, 0.95), 0.4), 2.0)
                        pts2, k2 = elbow_path(rng, p2, dd2, L2, (0.35, 0.8), (15, 30), None, 0, 0.1, sub=1, curl=0.03)
                        b2 = self.add(Br(br.level + 1, 'branch2', pts2, br, t2))
                        b2.kinks = k2

    def fill_shell(self):
        """Оболочка подкрон без ветвей рядом (> 1.1 м) получает ветвь от ближайшего узла скелета: каждый бугор кроны
        заполнен листвой, провалы остаются только между буграми."""
        rng = self.rng
        cr = self.crown
        forest = self.P['form'] == 'forest'
        S, owner = [], []

        def add_samples(b):
            n = max(2, int(b.length / 0.25))
            for k in range(n + 1):
                t = k / n
                if b.kind in ('limb', 'leader') and t < 0.25:
                    continue
                S.append(tuple(b.at(t)[0]))
                owner.append((b, t))

        for b in self.branches:
            if b.kind in ('limb', 'leader', 'branch', 'branch2'):
                add_samples(b)
        Sa = np.array(S)
        pts = cr.surface_points(0.8, rng)
        rng.shuffle(pts)
        added = 0
        limit = 35 if forest else 45
        for q, i, nq, uz in pts:
            if uz < -0.55 and rng.random() < (0.3 if forest else 0.2):
                continue
            inner = q - nq * rng.uniform(0.35, 0.9)
            d2 = ((Sa - np.asarray(tuple(inner))) ** 2).sum(1)
            j = int(np.argmin(d2))
            d = math.sqrt(d2[j])
            if d < 1.1 or d > 3.2:
                continue
            b, t = owner[j]
            p0 = b.at(t)[0]
            dirv = (inner - p0).normalized()
            if dirv.z < -0.6:
                continue
            pts_b, kinks = elbow_path(rng, p0, dirv, max(0.4, d - 0.15), (0.35, 0.8), (14, 26), inner, 0.4, 0.05,
                                      sub=1, curl=0.03)
            nb = self.add(Br(b.level + 1, 'branch2', pts_b, b, t))
            nb.kinks = kinks
            add_samples(nb)
            Sa = np.array(S)
            added += 1
            if added >= limit:
                break
        self.filled = added

    def add_stubs(self):
        """Сухие обломанные сучья на чистом стволе (самоочищение лесного дуба) и редкие водяные побеги."""
        rng = self.rng
        P = self.P
        forest = P['form'] == 'forest'
        n = rng.randint(4, 8) if forest else rng.randint(1, 3)
        zmax = self.crown_base * 0.95
        for k in range(n):
            z = rng.uniform(2.2 if forest else 1.4, zmax)
            t = self.t_at_z(self.trunk, z)
            p, d, _, _ = self.trunk.at(t)
            a = rng.uniform(0, math.tau)
            dd = (Vector((math.cos(a), math.sin(a), 0)) + UP * rng.uniform(-0.1, 0.5)).normalized()
            L = rng.uniform(0.12, 0.6)
            pts = [p, p + dd * L * 0.55, p + (dd - UP * 0.12).normalized() * L]
            br = self.add(Br(1, 'stub', pts, self.trunk, t))
            br.dead = True
        for k in range(rng.randint(1, 3) if forest else 0):
            z = rng.uniform(2.0, zmax * 0.92)
            t = self.t_at_z(self.trunk, z)
            a = rng.uniform(0, math.tau)
            self.epicormic.append((t, Vector((math.cos(a), math.sin(a), 0))))

    # --- толщины (трубочная модель) и вес ветра
    def depth_of(self, b):
        d = 0
        while b.parent is not None:
            d += 1
            b = b.parent
        return d

    def own_load(self, b):
        if b.kind in ('branch', 'branch2'):
            return b.length
        if b.kind in ('limb', 'leader'):
            return 0.35 * b.length
        return 0.0

    def cap_along(self, b, t):
        return self.own_load(b) * (1.0 - t) + sum(c.cap for c in b.children if c.t_par >= t - 1e-6 and not c.dead)

    def compute_radii(self):
        for b in sorted(self.branches, key=lambda b: -self.depth_of(b)):
            b.cap = self.own_load(b) + sum(c.cap for c in b.children if not c.dead)
        trunk = self.trunk
        t13 = self.t_at_z(trunk, 1.3)
        c13 = self.cap_along(trunk, t13)
        k = (self.P['dbh'] * 0.5) / c13 ** (1.0 / PIPE_N)
        self.k_pipe = k
        tip = {'branch': 0.009, 'branch2': 0.006, 'limb': 0.022, 'leader': 0.03}
        zf = trunk.pts[-1].z
        for b in sorted(self.branches, key=lambda b: self.depth_of(b)):
            if b.kind in ('stub', 'dead', 'broken'):
                continue
            radii = []
            for i, p in enumerate(b.pts):
                t = b.cum[i] / max(b.length, 1e-6)
                radii.append(k * max(self.cap_along(b, t), 1e-6) ** (1.0 / PIPE_N))
            sm = radii[:]
            for i in range(1, len(radii) - 1):
                sm[i] = 0.25 * radii[i - 1] + 0.5 * radii[i] + 0.25 * radii[i + 1]
            radii = sm
            mn = tip.get(b.kind, 0.01)
            radii = [max(r, mn * (0.75 if i == len(radii) - 1 else 1.0)) for i, r in enumerate(radii)]
            if b.kind == 'trunk':
                ref = (1 - 0.22 * 1.3 / zf)
                radii = [r * (1 - 0.22 * max(p.z, 0) / zf) / ref for r, p in zip(radii, b.pts)]
            if b.parent is not None:
                pr = b.parent.at(b.t_par)[2]
                lim = pr * (0.85 if b.kind == 'leader' else (0.8 if b.kind == 'limb' else 0.72))
                if b.kind == 'branch':
                    lim = min(lim, 0.055 if self.P['form'] == 'forest' else 0.068)
                elif b.kind == 'branch2':
                    lim = min(lim, 0.03)
                radii = [min(r, lim) for r in radii]
                # плавный сбег к концу (не «шип»): не тоньше прямой от основания до 25-30 % его толщины
                r0 = radii[0]
                rt = max(mn, r0 * (0.3 if b.kind in ('limb', 'leader') else 0.25))
                for i in range(len(radii)):
                    t = b.cum[i] / max(b.length, 1e-6)
                    radii[i] = max(radii[i], r0 + (rt - r0) * t)
                # монотонно не толще к концу
                for i in range(1, len(radii)):
                    radii[i] = min(radii[i], radii[i - 1])
            b.radii = radii
        for b in self.branches:
            if b.kind == 'stub':
                pr = b.parent.at(b.t_par)[2]
                r0 = min(pr * 0.2, self.rng.uniform(0.025, 0.055))
                b.radii = [r0, r0 * 0.8, r0 * 0.5]
            elif b.kind == 'dead':
                pr = b.parent.at(b.t_par)[2]
                r0 = min(pr * 0.7, 0.09)
                b.radii = [r0 * (1 - 0.75 * c / max(b.length, 1e-6)) for c in b.cum]
            elif b.kind == 'broken':
                pr = b.parent.at(b.t_par)[2]
                r0 = pr * 0.45
                b.radii = [r0 * (1 - 0.15 * c / max(b.length, 1e-6)) for c in b.cum]
        self.trunk_r13 = trunk.at(t13)[2]

    def compute_wind(self):
        cb = self.crown_base
        H = self.H
        dw = {'limb': 0.12, 'leader': 0.08, 'branch': 0.1, 'branch2': 0.08, 'dead': 0.06, 'stub': 0.0, 'broken': 0.0}
        for b in sorted(self.branches, key=lambda b: self.depth_of(b)):
            n = len(b.pts)
            if b.kind == 'trunk':
                b.w = [0.03 * min(1.0, max(0.0, (p.z - cb) / max(H - cb, 1))) ** 2 for p in b.pts]
                continue
            w0 = b.parent.at(b.t_par)[3]
            d = dw.get(b.kind, 0.1)
            b.w = [min(0.6 if b.kind in ('limb', 'leader') else 1.0, w0 + d * (b.cum[i] / max(b.length, 1e-6)) ** 1.5)
                   for i in range(n)]

    def assign_materials(self):
        for b in self.branches:
            if b.kind in ('trunk',) or (b.kind in ('limb', 'leader', 'broken') and b.radii[0] >= 0.04):
                b.mat = 'bark'
            elif b.kind in ('stub', 'dead'):
                b.mat = 'branch'
            else:
                b.mat = 'bark' if b.radii[0] >= BARK_MIN_R else 'branch'


# ------------------------------------------------------------------ меш

class NMesh(eg.MeshBuilder):
    """MeshBuilder со своими нормалями на углах и (для запекания креста) нормалью оболочки HN."""

    def __init__(self):
        super().__init__()
        self.normals = []
        self.hn = []

    def facen(self, idx, mat, uv0, uv1, col, nrm, hn=None):
        self.face(idx, mat, uv0, uv1, col)
        self.normals.append([tuple(n) for n in nrm])
        self.hn.append([tuple(n) for n in (hn or nrm)])

    def remap(self, mapping):
        self.faces = [(i, mapping[m], a, b, c) for (i, m, a, b, c) in self.faces]

    def used_materials(self):
        return sorted(set(f[1] for f in self.faces))

    def build(self, name, materials, with_hn=False):
        mesh = bpy.data.meshes.new(name)
        bm = bmesh.new()
        bverts = [bm.verts.new(v) for v in self.verts]
        bm.verts.ensure_lookup_table()
        uv0_layer = bm.loops.layers.uv.new('UVMap')
        uv1_layer = bm.loops.layers.uv.new('UV2')
        col_layer = bm.loops.layers.float_color.new('Col')
        hn_layer = bm.loops.layers.float_color.new('HN') if with_hn else None
        loop_n = []
        for (idx, mat, uv0, uv1, col), nrm, hn in zip(self.faces, self.normals, self.hn):
            try:
                f = bm.faces.new([bverts[i] for i in idx])
            except ValueError:
                continue
            f.material_index = mat
            for li, (loop, a, b, c) in enumerate(zip(f.loops, uv0, uv1, col)):
                loop[uv0_layer].uv = a
                loop[uv1_layer].uv = b
                loop[col_layer] = c
                if hn_layer is not None:
                    h = hn[li]
                    loop[hn_layer] = (h[0] * 0.5 + 0.5, h[1] * 0.5 + 0.5, h[2] * 0.5 + 0.5, 1.0)
            loop_n.extend(nrm)
        bm.to_mesh(mesh)
        bm.free()
        for m in materials:
            mesh.materials.append(m)
        mesh.polygons.foreach_set('use_smooth', [True] * len(mesh.polygons))
        mesh.normals_split_custom_set(loop_n)
        obj = bpy.data.objects.new(name, mesh)
        eg.link(obj)
        return obj

    @property
    def tris(self):
        return sum(len(f[0]) - 2 for f in self.faces)


def sweep(mb, br, mat, sides, H, keep=None, flare=None, bumpy=0.0, cap=True, tile=None, trunk_uv=False,
          collar=True, ragged=0.0, rng=None):
    """Кора ветви: кольца в узлах keep, рамка без скручивания. UV ствола: U = целые повторы по окружности + сдвиг,
    V = z / высота плитки (метры, плотность постоянна); у ветвей — квадратный тексель при текущей толщине.
    Наплыв у основания ветви (collar): первые кольца толще, начало утоплено в родителя."""
    TW, TH = tile
    idx = list(range(len(br.pts))) if keep is None else keep
    pts = [br.pts[i] for i in idx]
    rad = [br.radii[i] for i in idx]
    wts = [br.w[i] for i in idx]
    if collar and br.parent is not None and br.kind not in ('stub',) and len(pts) >= 2:
        # начало — внутри родителя, в 0.45 его радиуса от оси; кольцо у поверхности родителя — толще
        pr = br.parent.at(br.t_par)[2]
        d0 = (pts[1] - pts[0])
        L0 = d0.length
        if L0 > 1e-4:
            d0 /= L0
            s_in = min(pr * 0.45, L0 * 0.3)
            s_out = min(pr * 1.05 + rad[0] * 0.6, L0 * 0.6)
            p_in = br.pts[0] + d0 * s_in
            p_out = br.pts[0] + d0 * s_out
            r0 = rad[0]
            r_out = rad[0] + (rad[1] - rad[0]) * (s_out / L0)
            pts = [p_in, p_out] + pts[1:]
            rad = [r0 * 1.32, r_out * 1.12] + rad[1:]
            wts = [wts[0], wts[0]] + wts[1:]
    n = len(pts)
    if n < 2:
        return 0
    frames = []
    prev = None
    for i in range(n):
        if i == 0:
            t = (pts[1] - pts[0]).normalized()
        elif i == n - 1:
            t = (pts[-1] - pts[-2]).normalized()
        else:
            t = ((pts[i + 1] - pts[i]).normalized() + (pts[i] - pts[i - 1]).normalized()).normalized()
        if prev is None:
            nx = t.orthogonal().normalized() if abs(t.z) < 0.95 else Vector((1, 0, 0))
            nx = (nx - t * nx.dot(t)).normalized()
        else:
            nx = (prev - t * prev.dot(t))
            nx = nx.normalized() if nx.length > 1e-6 else t.orthogonal().normalized()
        prev = nx
        frames.append((t, nx, t.cross(nx).normalized()))
    if trunk_uv:
        r13 = br.radii[min(range(len(br.pts)), key=lambda i: abs(br.pts[i].z - 1.3))]
        utiles = max(1, round(2 * math.pi * r13 / TW))
    else:
        ref_i = min(n - 1, max(0, int(n * 0.15)))
        utiles = max(1, round(2 * math.pi * max(rad[ref_i], 0.004) / TW))
    v = 0.0
    rings = []
    tris = 0
    for i in range(n):
        t, nx, ny = frames[i]
        if trunk_uv:
            # V по высоте, но там, где ствол толще (наплывы у комля), реже: тексель квадратный, пластины у комля крупнее
            def r_eff(j):
                m = 1.0 if flare is None else sum(flare(pts[j].z, q * math.tau / 12) for q in range(12)) / 12
                return max(rad[j] * m, 1e-3)
            if i == 0:
                v = pts[0].z / TH
            else:
                v += (pts[i].z - pts[i - 1].z) * r13 / (0.5 * (r_eff(i) + r_eff(i - 1))) / TH
        elif i > 0:
            r_avg = max(0.5 * (rad[i] + rad[i - 1]), 0.003)
            v += (pts[i] - pts[i - 1]).length * utiles * TW / (2 * math.pi * r_avg * TH)
        r = max(rad[i], 0.0025)
        dr = 0.0
        if 0 < i < n - 1:
            dr = (rad[i + 1] - rad[i - 1]) / max((pts[i + 1] - pts[i - 1]).length, 1e-4)
        ring = []
        for s in range(sides + 1):
            a = s / sides * math.tau
            off = nx * math.cos(a) + ny * math.sin(a)
            rr = r
            if flare is not None:
                rr = r * flare(pts[i].z, a)
            if bumpy > 0:
                q = pts[i] + off * 0.6
                rr *= 1.0 + bumpy * noise.noise(Vector((q.x * 1.7, q.y * 1.7, q.z * 0.6)))
            p = pts[i] + off * rr
            if ragged > 0 and i == n - 1 and rng is not None:
                p += t * rr * rng.uniform(-ragged, ragged * 0.4)
            nrm = (off - t * dr).normalized()
            u = s / sides * utiles + (SHEAR * v if trunk_uv else 0.0)
            ring.append((mb.vert(p), (u, v), nrm))
        rings.append((ring, wts[i], pts[i].z))
    for i in range(n - 1):
        (ra, wa, za), (rb, wb, zb) = rings[i], rings[i + 1]
        for s in range(sides):
            a, b, c, d = ra[s], ra[s + 1], rb[s + 1], rb[s]
            mb.facen([a[0], b[0], c[0], d[0]], mat, [a[1], b[1], c[1], d[1]],
                     [(0, za / H), (0, za / H), (0, zb / H), (0, zb / H)],
                     [(0, 0, 0, wa), (0, 0, 0, wa), (0, 0, 0, wb), (0, 0, 0, wb)], [a[2], b[2], c[2], d[2]])
            tris += 2
    if cap and rad[-1] > 0.006:
        last, wl, zl = rings[-1]
        tdir = frames[-1][0]
        tip = mb.vert(pts[-1] + tdir * rad[-1] * (0.15 if ragged else 0.6))
        for s in range(sides):
            a, b = last[s], last[s + 1]
            mb.facen([a[0], b[0], tip], mat, [a[1], b[1], (a[1][0], a[1][1] + 0.03)], [(0, zl / H)] * 3,
                     [(0, 0, 0, wl)] * 3, [a[2], b[2], tdir])
            tris += 1
    return tris


def decimate_keep(br, max_angle_deg, min_step):
    pts = br.pts
    keep = [0]
    acc = 0.0
    last_d = (pts[1] - pts[0]).normalized()
    for i in range(1, len(pts) - 1):
        d = (pts[i + 1] - pts[i]).normalized()
        ang = math.degrees(last_d.angle(d, 0.0))
        acc += (pts[i] - pts[keep[-1]]).length
        if (i in br.kinks and ang > 6) or ang > max_angle_deg or acc > min_step:
            keep.append(i)
            last_d = d
            acc = 0.0
    keep.append(len(pts) - 1)
    return keep


def decimate_max(br, nseg):
    pts = br.pts
    n = len(pts)
    if n <= nseg + 1:
        return list(range(n))
    turn = []
    for i in range(1, n - 1):
        a = (pts[i] - pts[i - 1]).normalized()
        b = (pts[i + 1] - pts[i]).normalized()
        turn.append((a.angle(b, 0.0), i))
    turn.sort(reverse=True)
    keep = {0, n - 1}
    for ang, i in turn:
        if len(keep) >= nseg + 1:
            break
        keep.add(i)
    return sorted(keep)


# ------------------------------------------------------------------ карточки

class CardSet:
    """Карточки листвы: кандидаты в узлах ветвей (веер 1-3 побега), отбор равномерно по оболочке, ориентация."""

    def __init__(self, tree, meta, clump_meta):
        self.t = tree
        self.sk = tree.sk
        self.meta = meta
        self.cmeta = clump_meta
        self.rng = random.Random(tree.sk.seed + 77)

    def cell_size(self, cell):
        return self.meta['cells'][cell]['size']

    def candidates(self):
        sk, rng = self.sk, self.rng
        cr = sk.crown
        forest = sk.P['form'] == 'forest'
        shell = 2.0 if forest else 2.5
        cands = []
        for b in sk.branches:
            if b.kind not in ('branch', 'branch2', 'limb', 'leader') or b.dead:
                continue
            L = b.length
            step = 0.26 if b.kind in ('branch', 'branch2') else 0.36
            s0 = 0.2 * L if b.kind in ('branch', 'branch2') else 0.35 * L
            s = s0 + rng.uniform(0, step)
            nodes = []
            while s < L - 0.05:
                nodes.append(s / L)
                s += step * rng.uniform(0.8, 1.2)
            if b.kind in ('branch', 'branch2'):
                nodes.append(1.0)
            for t in nodes:
                p, d, r, w = b.at(t)
                dep, si = cr.depth(p)
                if dep > shell or dep < -0.9:
                    continue
                tip = t >= 0.999
                k = rng.choice([2, 3, 3, 4]) if tip else rng.choice([1, 2, 2, 3])
                az0 = rng.uniform(0, math.tau)
                for j in range(k):
                    if tip and j == 0:
                        sd = (d + UP * 0.15).normalized()
                    else:
                        ax = rot(d.orthogonal().normalized(), d, az0 + j * math.tau / k + rng.uniform(-0.4, 0.4))
                        sd = rot(d, ax, math.radians(rng.uniform(35, 70)))
                    cands.append(dict(p=p + (sd - d * sd.dot(d)).normalized() * r * 0.8 if r > 0.01 else p, d=sd,
                                      w=w, depth=dep, sub=si, br=b, t=t, z=p.z))
        # водяные побеги на стволе
        for t, out in sk.epicormic:
            p, d, r, w = sk.trunk.at(t)
            for k in range(rng.randint(1, 2)):
                dd = (out + Vector((rng.uniform(-0.5, 0.5), rng.uniform(-0.5, 0.5), rng.uniform(0.2, 0.9)))).normalized()
                cands.append(dict(p=p + out * r * 0.95, d=dd, w=0.02, depth=-1.0, sub=None, br=None, t=0, z=p.z,
                                  epicormic=True))
        return cands

    def orient(self, c, rng):
        """Ось «вдоль» и нормаль карточки: нормаль ~ нормаль подкроны + вверх (сбоку кроны карточки видны и с земли,
        и с воздуха), внизу кроны — «зонтиком» (лицом вверх, снизу видна изнанка), разброс наклона и крена."""
        cr = self.sk.crown
        if c.get('epicormic'):
            n = (horiz(c['d']) * 0.5 + UP).normalized()
            si = None
        else:
            si = c['sub'] if c['sub'] is not None else cr.fmin(c['p'])[1]
            h = cr.normal(c['p'], si)
            if h.z > -0.3:
                n = (h + UP * 0.7).normalized()
            else:
                n = (horiz(h) * 0.6 + UP).normalized()
        if not c.get('epicormic') and h.z > 0.5:  # верх кроны: часть веток наклонена во все стороны — сбоку видна
            n = rot(n, perp_axis(n, rng), math.radians(rng.uniform(10, 42)))
        else:
            n = rot(n, perp_axis(n, rng), math.radians(rng.gauss(0, 14)))
        a = c['d'] - n * c['d'].dot(n)
        if a.length < 0.2:
            a = horiz(c['d']) - n * horiz(c['d']).dot(n)
        a.normalize()
        n = rot(n, a, math.radians(rng.uniform(-25, 25)))
        return a, n, si

    def choose(self, n_target):
        """Отбор по оболочке: каждая точка поверхности каждой подкроны (шаг под число карточек) берёт ближайшую
        свободную ветку изнутри — листва ровно по всем буграм, без проплешин внизу кроны; потом второй слой глубже."""
        rng = random.Random(self.sk.seed + 5)
        cands = self.candidates()
        cr = self.sk.crown
        chosen = [c for c in cands if c.get('epicormic')]
        pool = [c for c in cands if not c.get('epicormic')]
        if not pool:
            return chosen
        s0 = self.cell_size(0)
        C = np.array([tuple(c['p'] + c['d'] * 0.35 * s0 * (0.85 if c['z'] < LOW_Z else 1.0)) for c in pool])
        dep = np.array([c['depth'] for c in pool])
        taken = np.zeros(len(pool), bool)
        surf = cr.surface_points(0.5, rng)
        # внутренние стенки провалов между подкронами (глубоко в общей оболочке кроны) — реже: листва снаружи
        Ps = np.array([tuple(s[0]) for s in surf])
        lo_s, hi_s = Ps.min(0), Ps.max(0)
        cen, rad = (lo_s + hi_s) * 0.5, np.maximum((hi_s - lo_s) * 0.5, 0.5)
        fg = np.sqrt((((Ps - cen) / rad) ** 2).sum(1))
        surf = [s for s, f in zip(surf, fg) if f > 0.8 or rng.random() < 0.35]
        area_pts = len(surf)
        for layer, (inset, share) in enumerate(((0.35, 0.72), (1.1, 0.28))):
            want = int(n_target * share) if layer == 0 else n_target - len(chosen)
            if want <= 0:
                break
            stride = max(1, int(round(area_pts / max(want, 1))))
            pts = surf[:]
            rng.shuffle(pts)
            if stride > 1:
                pts = pts[::stride]
            for q, i, nq, uz in pts:
                if len(chosen) >= n_target:
                    break
                tgt = np.asarray(tuple(q - nq * inset))
                d2 = ((C - tgt) ** 2).sum(1) + 0.15 * np.maximum(dep, 0) ** 2
                d2[taken] = 1e9
                j = int(np.argmin(d2))
                if d2[j] > 1.4 ** 2:
                    continue
                taken[j] = True
                chosen.append(pool[j])
        # остаток бюджета — самые неглубокие из оставшихся
        if len(chosen) < n_target:
            rest = [j for j in np.argsort(dep) if not taken[j]]
            for j in rest[:n_target - len(chosen)]:
                chosen.append(pool[j])
        return chosen

    def make_cards(self, chosen):
        """Параметры карточек LOD0 (по ним же LOD1)."""
        rng = random.Random(self.sk.seed + 9)
        cards = []
        for c in chosen:
            a, n, si = self.orient(c, rng)
            dep = c['depth']
            if c.get('epicormic'):
                cell, scale, fold = 3, rng.uniform(0.7, 0.9), math.radians(rng.uniform(25, 35))
            elif c['z'] < LOW_Z:
                cell = 3 if rng.random() < 0.55 else 2
                scale, fold = rng.uniform(0.85, 1.05), math.radians(rng.uniform(26, 36))
            else:
                r = rng.random()
                if dep > 1.0:
                    cell = 1 if r < 0.5 else (0 if r < 0.75 else 2)
                else:
                    cell = 0 if r < 0.55 else (1 if r < 0.8 else 2)
                scale = rng.uniform(0.9, 1.1) * (0.92 if dep > 1.0 else 1.0)
                fold = math.radians(rng.uniform(20, 34))
            cards.append(dict(p=c['p'], along=a, normal=n, cell=cell, scale=scale, fold=fold, w0=c['w'], sub=si,
                              depth=dep, z=c['z']))
        return cards

    def _corner_normals(self, p, n_card, si):
        cr = self.sk.crown
        if si is None:
            hn = n_card
        else:
            hn = cr.normal(p, si)
        nn = (n_card * 0.3 + hn * 0.7 + UP * 0.1).normalized()
        hb = (hn + UP * 0.15).normalized()
        return nn, hb

    def emit(self, mb, card, mat, H, folded=True):
        cell = self.meta['cells'][card['cell']]
        cols, rows = self.meta['grid']
        size = cell['size'] * card['scale']
        u0, v0, u1, v1 = cell['bbox']
        ua, va_att = cell['attach'][0], cell['bottom']
        vl, vr = cell['left'], cell['right']
        a = card['along']
        nrm = card['normal']
        side = a.cross(nrm).normalized()
        nrm = side.cross(a).normalized()
        fold = card['fold'] if folded else 0.0
        p_att = card['p']

        def pos(u, v):
            du = (u - ua) * size
            dv = (v - va_att) * size
            return p_att + a * dv + side * (du * math.cos(fold)) - nrm * (abs(du) * math.sin(fold))

        def uv(u, v):
            ci, cj = cell['col'], cell['row']
            return ((ci + u) / cols, 1 - (cj + 1 - v) / rows)

        w0 = card['w0']
        w1 = min(1.0, w0 + 0.15)
        if folded:
            P = dict(B=(ua, va_att), T=(ua, v1), L=(u0, vl), TL=(u0, v1), R=(u1, vr), TR=(u1, v1))
            quads = (('B', 'T', 'TL', 'L'), ('B', 'R', 'TR', 'T'))
        else:
            P = dict(BL=(u0, va_att), BR=(u1, va_att), TR=(u1, v1), TL=(u0, v1))
            quads = (('BL', 'BR', 'TR', 'TL'),)
        verts = {}
        for k, (u, v) in P.items():
            p = pos(u, v)
            f = (v - va_att) / max(v1 - va_att, 1e-4)
            ww = w0 + (w1 - w0) * f
            if k in ('L', 'TL', 'R', 'TR', 'BL', 'BR'):
                ww = min(1.0, ww + 0.03)
            nn, hb = self._corner_normals(p, nrm, card['sub'])
            verts[k] = (mb.vert(p), uv(u, v), (ww, ww, ww, ww), nn, p.z, hb)
        tris = 0
        for quad in quads:
            vs = [verts[k] for k in quad]
            mb.facen([x[0] for x in vs], mat, [x[1] for x in vs], [(0, x[4] / H) for x in vs], [x[2] for x in vs],
                     [x[3] for x in vs], [x[5] for x in vs])
            tris += 2
        return tris

    # --- «подкроны» LOD2
    def clumps(self, cards, n_target, scale_k=1.0):
        """Кластеры карточек LOD0 по подкронам (k-средних) -> карточки oak_clumps: центр кластера, размер по его
        разбросу, ориентация — как у листвы."""
        rng = random.Random(self.sk.seed + 21)
        cr = self.sk.crown
        groups = {}
        for c in cards:
            ctr = c['p'] + c['along'] * self.cell_size(c['cell']) * c['scale'] * 0.42
            si = c['sub'] if c['sub'] is not None else cr.fmin(ctr)[1]
            groups.setdefault(si, []).append(ctr)
        total = sum(len(v) for v in groups.values())
        out = []
        for si, P in groups.items():
            k = max(1, int(round(len(P) * n_target / max(total, 1))))
            X = np.array([tuple(p) for p in P])
            # детерминированный старт: самые далёкие точки
            idx = [0]
            for _ in range(1, min(k, len(X))):
                d = np.min(((X[:, None, :] - X[idx][None]) ** 2).sum(-1), 1)
                idx.append(int(np.argmax(d)))
            C = X[idx].copy()
            for _ in range(12):
                lab = np.argmin(((X[:, None, :] - C[None]) ** 2).sum(-1), 1)
                for j in range(len(C)):
                    m = X[lab == j]
                    if len(m):
                        C[j] = m.mean(0)
            lab = np.argmin(((X[:, None, :] - C[None]) ** 2).sum(-1), 1)
            for j in range(len(C)):
                m = X[lab == j]
                if not len(m):
                    continue
                ctr = Vector(tuple(C[j]))
                spread = math.sqrt(((m - C[j]) ** 2).sum(1).mean()) if len(m) > 1 else 0.3
                h = cr.normal(ctr, si)
                ctr = ctr + h * 0.15
                size = min(max((2.0 * spread + 0.95) * scale_k, 0.9), 2.8)
                if h.z > -0.3:
                    n = (h + UP * 0.7).normalized()
                else:
                    n = (horiz(h) * 0.6 + UP).normalized()
                n = rot(n, perp_axis(n, rng), math.radians(rng.uniform(15, 50) if h.z > 0.5 else rng.gauss(0, 12)))
                a = horiz(h) - n * horiz(h).dot(n)
                if a.length < 0.2:
                    a = UP - n * UP.dot(n)
                a.normalize()
                a = rot(a, n, rng.uniform(-0.6, 0.6))
                dep, _ = cr.depth(ctr)
                w = 0.25 + 0.15 * min(1.0, max(0.0, (ctr.z - self.sk.crown_base) / max(self.sk.H - self.sk.crown_base, 1)))
                out.append(dict(c=ctr, along=a, normal=n, size=size, cell=rng.randrange(4), sub=si, w0=w))
        return out

    def inner_fill(self, n_target, cards):
        """Внутренний слой кроны: карточки «подкрон» 1.4-2.2 м на глубине 0.7-1.7 м под поверхностью, рядом с
        ветвями (не дальше 1.5 м от узла с листвой) — тёмная масса листвы, сквозь которую не светит небо, дёшево
        (2 треугольника на ~1 м² листвы)."""
        rng = random.Random(self.sk.seed + 41)
        cr = self.sk.crown
        if n_target <= 0 or not cards:
            return []
        anchors = np.array([tuple(c['p']) for c in cards])
        surf = cr.surface_points(0.8, rng)
        rng.shuffle(surf)
        out = []
        placed = []
        for q, i, nq, uz in surf:
            if len(out) >= n_target:
                break
            p = q - nq * rng.uniform(1.0, 2.0)
            if not cr.inside(p):
                continue
            if ((anchors - np.asarray(tuple(p))) ** 2).sum(1).min() > 1.5 ** 2:
                continue
            if any((p - o).length < 0.95 for o in placed):
                continue
            h = cr.normal(p, i)
            nrm = (h + UP * 0.7).normalized() if h.z > -0.3 else (horiz(h) * 0.6 + UP).normalized()
            nrm = rot(nrm, perp_axis(nrm, rng), math.radians(rng.uniform(15, 50) if h.z > 0.5 else rng.gauss(0, 14)))
            a = horiz(h) - nrm * horiz(h).dot(nrm)
            if a.length < 0.2:
                a = UP - nrm * UP.dot(nrm)
            a = rot(a.normalized(), nrm, rng.uniform(-0.8, 0.8))
            w = 0.22 + 0.12 * min(1.0, max(0.0, (p.z - self.sk.crown_base) / max(self.sk.H - self.sk.crown_base, 1)))
            out.append(dict(c=p, along=a, normal=nrm, size=rng.uniform(1.2, 1.8), cell=rng.randrange(4), sub=i, w0=w))
            placed.append(p)
        return out

    def emit_clump(self, mb, cl, mat, H):
        cell = self.cmeta['cells'][cl['cell']]
        cols, rows = self.cmeta['grid']
        u0, v0, u1, v1 = cell['bbox']
        a = cl['along']
        n = cl['normal']
        side = a.cross(n).normalized()
        n = side.cross(a).normalized()
        S = cl['size']
        ci, cj = cell['col'], cell['row']
        verts = []
        for (u, v) in ((u0, v0), (u1, v0), (u1, v1), (u0, v1)):
            p = cl['c'] + side * ((u - 0.5) * S) + a * ((v - 0.5) * S)
            hn = self.sk.crown.normal(p, cl['sub'])
            nn = (n * 0.3 + hn * 0.7 + UP * 0.1).normalized()
            w = min(1.0, cl['w0'] + (0.12 if v > 0.5 else 0.0))
            verts.append((mb.vert(p), ((ci + u) / cols, 1 - (cj + 1 - v) / rows), (w, w, w, w), nn, p.z,
                          (hn + UP * 0.15).normalized()))
        mb.facen([x[0] for x in verts], mat, [x[1] for x in verts], [(0, x[4] / H) for x in verts],
                 [x[2] for x in verts], [x[3] for x in verts], [x[5] for x in verts])
        return 2


# ------------------------------------------------------------------ дерево и LOD

class OakTree:
    def __init__(self, name, P, meta, cmeta):
        self.name = name
        self.P = P
        self.sk = OakSkeleton(name, P)
        self.cs = CardSet(self, meta, cmeta)
        self.meta = meta
        fr = random.Random(self.sk.seed + 5)
        nb = fr.randint(5, 8)
        self.fl_ang = [fr.uniform(0, math.tau) for _ in range(nb)]
        self.fl_amp = [fr.uniform(0.15, 0.4) for _ in range(nb)]
        self.fl = 0.42 if P['form'] == 'forest' else 0.7

    def flare(self, z, a):
        k = math.exp(-max(z, 0.0) / 0.55)
        kb = math.exp(-max(z, 0.0) / 0.4)
        b = sum(am * max(0.0, math.cos(a - an)) ** 6 for am, an in zip(self.fl_amp, self.fl_ang))
        return 1.0 + self.fl * k + b * self.fl * kb

    def sides_for(self, b, lod):
        r0 = b.radii[0]
        if b.kind == 'trunk':
            return {0: 18 if self.P['form'] == 'open' else 16, 1: 10, 2: 6}[lod]
        if b.kind == 'log':
            return {0: 14, 1: 9, 2: 6}[lod]
        if b.kind == 'branch2':
            return 3
        if b.kind == 'branch' and lod == 0:
            return 4 if r0 > 0.03 else 3
        if b.kind in ('stub',):
            return 4 if lod == 0 else 0
        if b.kind in ('dead', 'broken'):
            return {0: 6 if r0 > 0.05 else 4, 1: 4, 2: 3}[lod]
        if lod == 0:
            if r0 > 0.25:
                return 11
            if r0 > 0.14:
                return 8
            if r0 > 0.08:
                return 7
            if r0 > 0.05:
                return 5
            if r0 > 0.03:
                return 4
            return 3
        if lod == 1:
            return 7 if r0 > 0.2 else (6 if r0 > 0.1 else (5 if r0 > 0.05 else (4 if r0 > 0.03 else 3)))
        return 5 if r0 > 0.2 else (4 if r0 > 0.08 else 3)

    def bark(self, mb, lod, mi):
        sk = self.sk
        H = sk.H
        tris = 0
        l2 = sorted([b for b in sk.branches if b.kind == 'branch'], key=lambda b: -b.radii[0])
        keep1 = set(id(b) for b in l2[:int(len(l2) * 0.38)])
        limbs = sorted([b for b in sk.branches if b.kind in ('limb', 'leader', 'dead', 'broken')], key=lambda b: -b.radii[0])
        keep2 = set(id(b) for b in limbs[:14])
        brng = random.Random(sk.seed + 31)
        kt = {}
        for b in sk.branches:
            if lod == 1 and (b.kind == 'branch2' or (b.kind == 'branch' and id(b) not in keep1) or b.kind == 'stub'):
                continue
            if lod == 2 and (b.kind in ('branch', 'branch2', 'stub') or (b.kind not in ('trunk', 'log') and id(b) not in keep2)):
                continue
            sides = self.sides_for(b, lod)
            if sides == 0:
                continue
            if lod == 0:
                keep = None
            elif lod == 1:
                keep = decimate_keep(b, 14, 1.8) if b.kind in ('trunk', 'log', 'limb', 'leader') else decimate_max(b, 2)
            else:
                keep = decimate_keep(b, 25, 3.5) if b.kind in ('trunk', 'log') else decimate_max(b, 3)
            mat = mi[b.mat] if b.mat in mi else mi['bark']
            tile = ot.BARK_TILE if b.mat == 'bark' else ot.BRANCH_TILE
            if b.kind == 'trunk':
                snag_top = bool(self.P.get('snag') or self.P.get('broken_at'))
                tt = sweep(mb, b, mat, sides, H, keep, flare=self.flare,
                           bumpy=(0.05 if self.P['form'] == 'open' else 0.025), cap=snag_top, tile=ot.BARK_TILE,
                           trunk_uv=True, collar=False, ragged=(0.8 if snag_top else 0.0), rng=brng)
            else:
                tt = sweep(mb, b, mat, sides, H, keep, bumpy=(0.03 if b.radii[0] > 0.1 else 0.0), tile=tile,
                           collar=(lod < 2 and b.kind not in ('stub', 'branch2') and b.radii[0] > 0.035),
                           ragged=(0.6 if b.kind in ('stub', 'broken', 'log') else 0.0), rng=brng)
            tris += tt
            kt[b.kind] = kt.get(b.kind, 0) + tt
        self.kind_tris = getattr(self, 'kind_tris', {})
        self.kind_tris[lod] = kt
        return tris


def build_lods(tree, mats_by_id):
    """LOD0-2: меши (NMesh), треугольники, список материалов каждого LOD."""
    P = tree.P
    budget = P['budget']
    cs = tree.cs
    res = {}
    ids_all = ['oak_bark', 'oak_branch', 'oak_leaves', 'oak_clumps']
    mi = {'bark': 0, 'branch': 1}
    if P.get('dead'):
        # сухостой: только кора, у LOD — та же разрядка ветвей, что у живого
        for lod in range(3):
            mb = NMesh()
            bark = tree.bark(mb, lod, mi)
            res[lod] = dict(mb=mb, bark=bark, cards=0, n_cards=0, ids=None)
        tree.cards0, tree.cards1, tree.inner = [], [], []
        tree.lod1_scale = 1.0
        return res
    # LOD0
    mb0 = NMesh()
    bark0 = tree.bark(mb0, 0, mi)
    # внутренний слой «подкрон» (2 треугольника) — доля бюджета листвы; снаружи — ветки атласа: внешние и у земли
    # сложенные (4 треугольника), глубже — плоские
    fill_tris = int((budget - bark0) * P.get('fill_share', 0.16))
    n0 = max(300, int((budget - bark0 - fill_tris) / 2.8))
    chosen = cs.choose(n0)
    cards_all = cs.make_cards(chosen)
    cards = []
    tcount = mb0.tris
    for c in cards_all:
        folded = c['depth'] < 0.35 or c['z'] < LOW_Z + 1.0 or c['cell'] == 3
        need = 4 if folded else 2
        if tcount + need > budget - fill_tris:
            break
        c['folded'] = folded
        tcount += cs.emit(mb0, c, 2, tree.sk.H, folded=folded)
        cards.append(c)
    inner = cs.inner_fill((budget - tcount) // 2, cards)
    for cl in inner:
        cs.emit_clump(mb0, cl, 3, tree.sk.H)
    res[0] = dict(mb=mb0, bark=bark0, cards=mb0.tris - bark0, n_cards=len(cards) + len(inner))
    # LOD1: те же карточки плоскими (ближние к поверхности; если все не помещаются — оставшиеся чуть крупнее, до
    # 1.2x: на 35-85 м лист — 1-2 пикселя, а покрытие кроны не падает) и тот же внутренний слой
    lod0_tris = mb0.tris
    target1 = int(round(lod0_tris * 0.385))
    mb1 = NMesh()
    bark1 = tree.bark(mb1, 1, mi)
    for cl in inner:
        cs.emit_clump(mb1, cl, 3, tree.sk.H)
    n1 = min(len(cards), max(100, (target1 - mb1.tris) // 2))
    keep1 = sorted(cards, key=lambda c: c['depth'])[:n1]
    s1 = min(1.12, math.sqrt(len(cards) / max(n1, 1)))
    for c in keep1:
        cs.emit(mb1, dict(c, scale=c['scale'] * s1), 2, tree.sk.H, folded=False)
    tree.lod1_scale = s1
    res[1] = dict(mb=mb1, bark=bark1, cards=mb1.tris - bark1, n_cards=len(keep1) + len(inner))
    tree.cards0 = cards
    tree.cards1 = keep1
    tree.inner = inner
    # LOD2: «подкроны»
    target2 = int(round(lod0_tris * 0.135))
    mb2p = NMesh()
    bark2 = tree.bark(mb2p, 2, mi)
    tree.n_clumps = max(60, (target2 - bark2) // 2)
    tree.bark2 = bark2
    for k, d in res.items():
        d['ids'] = None
    return res


def make_lod2(tree, scale_k):
    mi = {'bark': 0, 'branch': 1}
    mb2 = NMesh()
    bark2 = tree.bark(mb2, 2, mi)
    cls = tree.cs.clumps(tree.cards0, tree.n_clumps, scale_k)
    for cl in cls:
        tree.cs.emit_clump(mb2, cl, 3, tree.sk.H)
    return dict(mb=mb2, bark=bark2, cards=mb2.tris - bark2, n_cards=len(cls))


def finalize_ids(d, ids_all):
    used = d['mb'].used_materials()
    mapping = {m: i for i, m in enumerate(used)}
    d['mb'].remap(mapping)
    d['ids'] = [ids_all[m] for m in used]
    return d


# ------------------------------------------------------------------ материалы (рендер покрытия, запекание креста)

def bake_material(name, albedo, alpha=False, cutoff=0.5, ao_w=0.0, ao_dist=2.0, gain=1.0):
    """PASS 0 — альбедо x lerp(1, AO, ao_w) (эмиссия), 1 — нормаль оболочки HN в пространстве камеры, 2 — белая маска
    покрытия. Альфа — по порогу cutoff."""
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    N.clear()
    out = N.new('ShaderNodeOutputMaterial')
    pas = N.new('ShaderNodeValue')
    pas.name = 'PASS'
    uv = N.new('ShaderNodeUVMap')
    uv.uv_map = 'UVMap'
    ta = N.new('ShaderNodeTexImage')
    ta.image = bpy.data.images.load(albedo, check_existing=True)
    ta.image.alpha_mode = 'STRAIGHT'
    L.new(uv.outputs[0], ta.inputs[0])
    col = ta.outputs['Color']
    if ao_w > 0:
        ao = N.new('ShaderNodeAmbientOcclusion')
        ao.samples = 24
        ao.inputs['Distance'].default_value = ao_dist
        ma = N.new('ShaderNodeMath')
        ma.operation = 'MULTIPLY_ADD'
        ma.inputs[1].default_value = ao_w * gain
        ma.inputs[2].default_value = (1.0 - ao_w) * gain
        L.new(ao.outputs['AO'], ma.inputs[0])
        sc = N.new('ShaderNodeVectorMath')
        sc.operation = 'SCALE'
        L.new(col, sc.inputs[0])
        L.new(ma.outputs[0], sc.inputs['Scale'])
        col = sc.outputs[0]
    geo = N.new('ShaderNodeNewGeometry')
    vt = N.new('ShaderNodeVectorTransform')
    vt.vector_type = 'NORMAL'
    vt.convert_from = 'WORLD'
    vt.convert_to = 'CAMERA'
    L.new(geo.outputs['Normal'], vt.inputs[0])
    enc = N.new('ShaderNodeVectorMath')
    enc.operation = 'MULTIPLY_ADD'
    enc.inputs[1].default_value = (0.5, 0.5, -0.5)
    enc.inputs[2].default_value = (0.5, 0.5, 0.5)
    L.new(vt.outputs[0], enc.inputs[0])
    sels = []
    for tv in (1.0, 2.0):
        c = N.new('ShaderNodeMath')
        c.operation = 'COMPARE'
        c.inputs[1].default_value = tv
        c.inputs[2].default_value = 0.1
        L.new(pas.outputs[0], c.inputs[0])
        sels.append(c.outputs[0])
    m1 = N.new('ShaderNodeMix')
    m1.data_type = 'RGBA'
    L.new(sels[0], m1.inputs['Factor'])
    L.new(col, m1.inputs['A'])
    L.new(enc.outputs[0], m1.inputs['B'])
    m2 = N.new('ShaderNodeMix')
    m2.data_type = 'RGBA'
    L.new(sels[1], m2.inputs['Factor'])
    L.new(m1.outputs['Result'], m2.inputs['A'])
    m2.inputs['B'].default_value = (1, 1, 1, 1)
    em = N.new('ShaderNodeEmission')
    L.new(m2.outputs['Result'], em.inputs['Color'])
    surf = em.outputs[0]
    if alpha:
        clip = N.new('ShaderNodeMath')
        clip.operation = 'GREATER_THAN'
        clip.inputs[1].default_value = cutoff
        L.new(ta.outputs['Alpha'], clip.inputs[0])
        tr = N.new('ShaderNodeBsdfTransparent')
        mx = N.new('ShaderNodeMixShader')
        L.new(clip.outputs[0], mx.inputs['Fac'])
        L.new(tr.outputs[0], mx.inputs[1])
        L.new(surf, mx.inputs[2])
        surf = mx.outputs[0]
    L.new(surf, out.inputs['Surface'])
    return m


def set_pass(v):
    for m in bpy.data.materials:
        if m.node_tree and 'PASS' in m.node_tree.nodes:
            m.node_tree.nodes['PASS'].outputs[0].default_value = float(v)


def obj_bounds(objs):
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
    return (Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts))),
            Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts))))


def coverage(scene, obj, tmp, res=192):
    """Доля непрозрачного в рамке дерева: вид спереди, сбоку и сверху под 40° (орто, маска)."""
    lo, hi = obj_bounds([obj])
    size = max(hi.z - lo.z, hi.x - lo.x, hi.y - lo.y) * 1.1
    cz = (lo.z + hi.z) * 0.5
    set_pass(2)
    eg.setup_card_render(scene, res, res, samples=4)
    ot._use_gpu(scene)
    scene.cycles.transparent_max_bounces = 64
    scene.view_settings.view_transform = 'Raw'
    vals = []
    for k, (loc, rv) in enumerate((((0, -200, cz), (math.pi / 2, 0, 0)), ((200, 0, cz), (math.pi / 2, 0, math.pi / 2)),
                                   ((0, -200 * math.cos(math.radians(40)), cz + 200 * math.sin(math.radians(40))),
                                    (math.radians(50), 0, 0)))):
        cam = eg.ortho_camera(scene, loc, rv, size, name='covcam')
        path = os.path.join(tmp, f'cov_{k}.png')
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        a = ot._load_raw(path)[..., 3]
        vals.append(float((a > 0.5).mean()))
        bpy.data.objects.remove(cam)
    return vals


def area_resample(img, oh, ow):
    """Усреднение по площади (вверх и вниз), по осям."""
    def axis(a, n_out, ax):
        n_in = a.shape[ax]
        cs = np.cumsum(np.concatenate([np.zeros_like(np.take(a, [0], ax)), a], ax), ax)
        edges = np.linspace(0, n_in, n_out + 1)
        i0 = np.floor(edges).astype(int)
        f = edges - i0
        i0 = np.clip(i0, 0, n_in)
        i1 = np.clip(i0 + 1, 0, n_in)
        c0 = np.take(cs, i0, ax)
        c1 = np.take(cs, i1, ax)
        shape = [1] * a.ndim
        shape[ax] = -1
        val = c0 + (c1 - c0) * f.reshape(shape)
        sl0 = [slice(None)] * a.ndim
        sl1 = [slice(None)] * a.ndim
        sl0[ax] = slice(0, n_out)
        sl1[ax] = slice(1, n_out + 1)
        w = np.diff(edges).reshape(shape)
        return (val[tuple(sl1)] - val[tuple(sl0)]) / w
    return axis(axis(img, oh, 0), ow, 1)


def render_cross(scene, obj, folder, prefix, slot=512, samples=48, crown=None):
    """Атлас креста 2048x512: 0 — вид спереди (камера с -Y), 1 — сбоку (с +X), 2 — сзади (с +Y), 3 — сверху.
    Каждый вид — по рамке дерева в этом направлении (неквадратный кадр растянут в 512x512). Альбедо x AO, нормаль —
    оболочки подкрон (HN)."""
    lo, hi = obj_bounds([obj])
    lo = Vector((lo.x - 0.1, lo.y - 0.1, lo.z))
    hi = Vector((hi.x + 0.1, hi.y + 0.1, hi.z + 0.1))
    tmp = os.path.join(folder, '_cross_tmp')
    os.makedirs(tmp, exist_ok=True)
    eg.setup_card_render(scene, 64, 64, samples=samples)
    ot._use_gpu(scene)
    scene.cycles.transparent_max_bounces = 96
    scene.render.filter_size = 1.2
    hull = None
    if crown is not None:
        hmat = eg.view_normal_material('hull_normal')
        hull = []
        for c, r in zip(crown.cl, crown.rl):
            me = bpy.data.meshes.new('hull')
            bmh = bmesh.new()
            bmesh.ops.create_uvsphere(bmh, u_segments=32, v_segments=16, radius=1.0)
            bmh.to_mesh(me)
            bmh.free()
            me.polygons.foreach_set('use_smooth', [True] * len(me.polygons))
            me.materials.append(hmat)
            ho = bpy.data.objects.new('hull', me)
            eg.link(ho)
            ho.matrix_world = Matrix.Translation(c) @ Matrix.Diagonal((r[0], r[1], r[2], 1.0))
            ho.hide_render = True
            hull.append(ho)
    cx, cy, cz = (lo.x + hi.x) / 2, (lo.y + hi.y) / 2, (lo.z + hi.z) / 2
    views = [('front', (cx, lo.y - 60, cz), (math.pi / 2, 0, 0), hi.x - lo.x, hi.z - lo.z),
             ('side', (hi.x + 60, cy, cz), (math.pi / 2, 0, math.pi / 2), hi.y - lo.y, hi.z - lo.z),
             ('back', (cx, hi.y + 60, cz), (math.pi / 2, 0, math.pi), hi.x - lo.x, hi.z - lo.z),
             ('top', (cx, cy, hi.z + 60), (0, 0, 0), hi.x - lo.x, hi.y - lo.y)]
    al = np.zeros((slot, slot * 4, 4), np.float32)
    nm = np.zeros((slot, slot * 4, 3), np.float32)
    for k, (nmv, loc, rv, wx, wy) in enumerate(views):
        big = 1024
        rx = max(64, int(round(big * wx / max(wx, wy))))
        ry = max(64, int(round(big * wy / max(wx, wy))))
        scene.render.resolution_x, scene.render.resolution_y = rx, ry
        cam = eg.ortho_camera(scene, loc, rv, max(wx, wy), name=f'xc_{nmv}')
        cam.data.clip_end = 1000
        res = []
        for p in (0, 1):
            set_pass(p)
            scene.view_settings.view_transform = 'Standard' if p == 0 else 'Raw'
            path = os.path.join(tmp, f'{prefix}_{nmv}_{p}.png')
            scene.render.filepath = path
            bpy.ops.render.render(write_still=True)
            res.append(eg.srgb_png_to_linear_values(path) if p == 0 else ot._load_raw(path))
        A = res[0]
        pm = np.concatenate([A[..., :3] * A[..., 3:4], A[..., 3:4]], -1)
        pr = area_resample(pm, slot, slot)
        a = pr[..., 3]
        rgb = pr[..., :3] / np.maximum(a[..., None], 1e-5)
        ng = res[1][..., :3] * 2 - 1
        if hull is not None:  # нормаль оболочки подкрон, где она есть; ветви и ствол вне её — свои
            for o in hull:
                o.hide_render = False
            obj.hide_render = True
            scene.view_settings.view_transform = 'Raw'
            hp = os.path.join(tmp, f'{prefix}_{nmv}_hull.png')
            scene.render.filepath = hp
            bpy.ops.render.render(write_still=True)
            hr = ot._load_raw(hp)
            for o in hull:
                o.hide_render = True
            obj.hide_render = False
            hn = hr[..., :3] * 2 - 1
            ha = (hr[..., 3:4] > 0.5).astype(np.float32)
            ng = ng * (1 - ha) + (hn * 0.45 + ng * 0.55) * ha
        bpy.data.objects.remove(cam)
        nn = area_resample(ng * A[..., 3:4] + np.array([0, 0, 1e-3], np.float32), slot, slot)
        nn /= np.maximum(np.linalg.norm(nn, axis=-1, keepdims=True), 1e-5)
        al[:, k * slot:(k + 1) * slot, :3] = rgb
        al[:, k * slot:(k + 1) * slot, 3] = a
        nm[:, k * slot:(k + 1) * slot] = nn * 0.5 + 0.5
    a = al[..., 3]
    al[..., 3] = np.clip(a * 1.08, 0, 1)
    al[..., :3] = ot.push_pull_fill(al[..., :3], a)
    nm = np.where(a[..., None] > 0.02, nm, np.array([0.5, 0.5, 1.0], np.float32))
    nm = ot.push_pull_fill(nm, a)
    names = [prefix + '_albedo.png', prefix + '_normal.png']
    eg.save_png(al, os.path.join(folder, names[0]))
    eg.save_png(nm, os.path.join(folder, names[1]), linear=True)
    shutil.rmtree(tmp, ignore_errors=True)
    for o in (hull or []):
        bpy.data.objects.remove(o)
    return names, dict(lo=lo, hi=hi)


def cross_mesh(info, name, material, z_top, H):
    """Крест 24 треугольника: плоскость XZ (лицо — к камере вида 0 с -Y, изнанка — вид 2), плоскость YZ (лицо — вид 1
    с +X, изнанка — он же зеркально: силуэт в орто тот же), горизонтальная сверху (вид 3, 2x2). Два ряда по высоте —
    для изгиба ветром. Нормали вершин — нормали плоскостей (рельеф кроны — в карте нормалей)."""
    lo, hi = info['lo'], info['hi']
    mb = NMesh()
    zs = [lo.z, min(max(H * 0.42, lo.z + 1.0), hi.z - 1.0), hi.z]

    def wind(z):
        a = 0.25 * max(0.0, min(1.0, z / max(hi.z, 1.0))) ** 1.5
        return (a, a, a, a)

    def U(slot, u):
        return (slot + min(max(u, 0.0), 1.0)) / 4.0

    def quad(ps, uvs, n):
        idx = [mb.vert(p) for p in ps]
        mb.facen(idx, 0, uvs, uvs, [wind(p.z) for p in ps], [n] * 4)

    wx, wy, wz = hi.x - lo.x, hi.y - lo.y, hi.z - lo.z
    for j in range(2):
        z0, z1 = zs[j], zs[j + 1]
        v0, v1 = (z0 - lo.z) / wz, (z1 - lo.z) / wz
        # плоскость A (y = 0): лицо к -Y
        ps = [Vector((lo.x, 0, z0)), Vector((hi.x, 0, z0)), Vector((hi.x, 0, z1)), Vector((lo.x, 0, z1))]
        quad(ps, [(U(0, 0), v0), (U(0, 1), v0), (U(0, 1), v1), (U(0, 0), v1)], (0, -1, 0))
        ps_b = [Vector((hi.x, 0.001, z0)), Vector((lo.x, 0.001, z0)), Vector((lo.x, 0.001, z1)), Vector((hi.x, 0.001, z1))]
        quad(ps_b, [(U(2, 0), v0), (U(2, 1), v0), (U(2, 1), v1), (U(2, 0), v1)], (0, 1, 0))
        # плоскость B (x = 0): лицо к +X (вправо в кадре — +Y)
        ps = [Vector((0, lo.y, z0)), Vector((0, hi.y, z0)), Vector((0, hi.y, z1)), Vector((0, lo.y, z1))]
        quad(ps, [(U(1, 0), v0), (U(1, 1), v0), (U(1, 1), v1), (U(1, 0), v1)], (1, 0, 0))
        ps_b = [Vector((-0.001, hi.y, z0)), Vector((-0.001, lo.y, z0)), Vector((-0.001, lo.y, z1)), Vector((-0.001, hi.y, z1))]
        quad(ps_b, [(U(1, 1), v0), (U(1, 0), v0), (U(1, 0), v1), (U(1, 1), v1)], (-1, 0, 0))
    # верх
    for i in range(2):
        for j in range(2):
            xa, xb = lo.x + wx * i / 2, lo.x + wx * (i + 1) / 2
            ya, yb = lo.y + wy * j / 2, lo.y + wy * (j + 1) / 2
            ps = [Vector((xa, ya, z_top)), Vector((xb, ya, z_top)), Vector((xb, yb, z_top)), Vector((xa, yb, z_top))]
            uvs = [(U(3, (p.x - lo.x) / wx), (p.y - lo.y) / wy) for p in ps]
            quad(ps, uvs, (0, 0, 1))
    return mb.build(name, [material])


# ------------------------------------------------------------------ выход

def make_textures(tex_dir, force=False):
    os.makedirs(tex_dir, exist_ok=True)
    need = ['oak_bark_albedo.png', 'oak_branch_albedo.png', 'oak_leaves_albedo.png', 'oak_leaves_cells.json',
            'oak_clumps_albedo.png', 'oak_clumps_cells.json']
    if force or not all(os.path.exists(os.path.join(tex_dir, n)) for n in need):
        ot.make_all(tex_dir)
    with open(os.path.join(tex_dir, 'oak_leaves_cells.json'), encoding='utf-8') as f:
        meta = json.load(f)
    with open(os.path.join(tex_dir, 'oak_clumps_cells.json'), encoding='utf-8') as f:
        cmeta = json.load(f)
    return meta, cmeta


def mesh_stats(obj):
    me = obj.data
    zs = [v.co.z for v in me.vertices]
    return dict(tris=sum(len(p.vertices) - 2 for p in me.polygons), zmin=min(zs), zmax=max(zs))


def build_asset(name, out_root, tex_dir, meta, cmeta):
    P = PRESETS[name]
    eg.reset_scene()
    scene = bpy.context.scene
    folder = os.path.join(out_root, name)
    if os.path.isdir(folder):
        for f in os.listdir(folder):
            fp = os.path.join(folder, f)
            if os.path.isfile(fp):
                os.remove(fp)
    os.makedirs(folder, exist_ok=True)
    tmp = os.path.join(folder, '_tmp')
    os.makedirs(tmp, exist_ok=True)
    T = lambda n: os.path.join(tex_dir, n)  # noqa: E731
    variant = name[4:]
    cross_id = f'oak_cross_{variant}'
    dead = bool(P.get('dead'))
    ids_all = DEAD_IDS + ['oak_leaves', 'oak_clumps'] if dead else ['oak_bark', 'oak_branch', 'oak_leaves', 'oak_clumps']
    bm = {
        ids_all[0]: bake_material('b_bark', T(ids_all[0] + '_albedo.png'), ao_w=0.45, gain=0.92),
        ids_all[1]: bake_material('b_branch', T(ids_all[1] + '_albedo.png'), ao_w=0.45, gain=0.92),
        'oak_leaves': bake_material('b_leaves', T('oak_leaves_albedo.png'), alpha=True, cutoff=0.5, ao_w=0.45, gain=0.92),
        'oak_clumps': bake_material('b_clumps', T('oak_clumps_albedo.png'), alpha=True, cutoff=0.45, ao_w=0.0),
    }
    tree = OakTree(name, P, meta, cmeta)
    sk = tree.sk
    lods = build_lods(tree, bm)
    # LOD2: подгонка размера «подкрон» по покрытию LOD1
    finalize_ids(lods[1], ids_all)
    o1 = lods[1]['mb'].build(f'{name}_LOD1cov', [bm[i] for i in lods[1]['ids']])
    cov1 = coverage(scene, o1, tmp)
    bpy.data.objects.remove(o1)
    k = 1.0
    hist = []
    for it in range(0 if dead else 6):
        d2 = make_lod2(tree, k)
        finalize_ids(d2, ids_all)
        o2 = d2['mb'].build(f'{name}_LOD2cov', [bm[i] for i in d2['ids']])
        cov2 = coverage(scene, o2, tmp)
        bpy.data.objects.remove(o2)
        ratio = sum(cov1) / max(sum(cov2), 1e-4)
        hist.append((round(k, 3), [round(c, 3) for c in cov2]))
        if abs(ratio - 1) < 0.03:
            break
        k *= ratio ** 0.65
    if dead:
        finalize_ids(lods[2], ids_all)
    else:
        lods[2] = d2
    print('COVERAGE', name, 'LOD1', [round(c, 3) for c in cov1], 'LOD2 tries', hist)
    finalize_ids(lods[0], ids_all)
    # объекты для экспорта
    objs = {}
    for i in range(3):
        d = lods[i]
        objs[i] = d['mb'].build(f'{name}_LOD{i}', [bm[x] for x in d['ids']])
    # крест — с LOD1 (тот же меш, с нормалью оболочки HN)
    ob = lods[1]['mb'].build(f'{name}_bake', [bm[x] for x in lods[1]['ids']])
    for o in objs.values():
        o.hide_render = True
    names_x, info = render_cross(scene, ob, folder, cross_id, crown=sk.crown)
    bpy.data.objects.remove(ob)
    m_cross = bpy.data.materials.new(cross_id)
    R = 0.25 * ((info['hi'].x - info['lo'].x) + (info['hi'].y - info['lo'].y))
    zt = info['hi'].z
    z_top = max(sk.crown_base + 0.62 * (zt - sk.crown_base), zt - 0.6 * R)
    cross = cross_mesh(info, f'{name}_LOD3', m_cross, z_top, zt)
    files = []
    stats = []
    for i, o in enumerate([objs[0], objs[1], objs[2], cross]):
        fn = f'{name}_LOD{i}.fbx'
        eg.export_fbx([o], os.path.join(folder, fn))
        files.append(fn)
        st = mesh_stats(o)
        if i < 3:
            st.update(bark=lods[i]['bark'], cards=lods[i]['cards'], n_cards=lods[i]['n_cards'])
        stats.append(st)
    shutil.rmtree(tmp, ignore_errors=True)
    tex_files = []
    for n in ((ids_all[0], ids_all[1]) if dead else ('oak_bark', 'oak_branch', 'oak_leaves', 'oak_clumps')):
        for s in ('albedo', 'normal', 'mask'):
            shutil.copy2(T(f'{n}_{s}.png'), os.path.join(folder, f'{n}_{s}.png'))
            tex_files.append(f'{n}_{s}.png')
    height = round(stats[0]['zmax'], 2)
    lo, hi = sk.crown.extents(random.Random(1))
    crown_r = round(0.25 * ((hi[0] - lo[0]) + (hi[1] - lo[1])), 2)
    wind_tree = dict(stiffness=1.5, drag=0.07, initialBend=1.0, heightDrag=0.0)
    leaf_common = dict(alphaCutoff=0.5, normalScale=0.8, aoMin=0.35, aoMax=1.0, smoothnessMax=0.3, specular=0.3,
                       healthyColor=[1.0, 1.0, 1.0], dryColor=[0.86, 0.88, 0.76], colorNoiseSpread=0.3,
                       alphaDrag=0.0, shiverDrag=0.06, shiverDirectionality=0.5, puddleSink=0.0, rotorWashClear=0.0,
                       sizeVariation=0.0, **wind_tree)
    if dead:
        # сухостой стоит жёстко: ветви без листвы почти не качаются
        wind_tree = dict(stiffness=3.0, drag=0.03, initialBend=0.5, heightDrag=0.0)
    materials = [
        dict(id=ids_all[0], role='bark', albedo=ids_all[0] + '_albedo.png', normal=ids_all[0] + '_normal.png',
             mask=ids_all[0] + '_mask.png', tiling=[1, 1], normalScale=1.2, smoothnessMax=1.0, aoMax=1.0, **wind_tree),
        dict(id=ids_all[1], role='bark', albedo=ids_all[1] + '_albedo.png', normal=ids_all[1] + '_normal.png',
             mask=ids_all[1] + '_mask.png', tiling=[1, 1], normalScale=0.9, smoothnessMax=1.0, aoMax=1.0, **wind_tree),
        dict(id='oak_leaves', role='leaves', albedo='oak_leaves_albedo.png', normal='oak_leaves_normal.png',
             mask='oak_leaves_mask.png', **leaf_common),
        dict(id='oak_clumps', role='leaves', albedo='oak_clumps_albedo.png', normal='oak_clumps_normal.png',
             mask='oak_clumps_mask.png', **dict(leaf_common, alphaCutoff=0.45, normalScale=0.6, shiverDrag=0.03)),
        dict(id=cross_id, role='cross', albedo=names_x[0], normal=names_x[1], alphaCutoff=0.5, smoothnessMax=0.15,
             specular=0.25, healthyColor=[1.0, 1.0, 1.0], dryColor=[0.86, 0.88, 0.76], colorNoiseSpread=0.3,
             normalScale=1.0, **wind_tree),
    ]
    used = set(x for i in range(3) for x in lods[i]['ids'])
    materials = [m for m in materials if m['id'] in used or m['role'] == 'cross']
    lod_entries = [dict(fbx=files[0], screen=LOD_SCREEN[0], submeshes=lods[0]['ids'], castShadows=True, triangles=stats[0]['tris']),
                   dict(fbx=files[1], screen=LOD_SCREEN[1], submeshes=lods[1]['ids'], castShadows=True, triangles=stats[1]['tris']),
                   dict(fbx=files[2], screen=LOD_SCREEN[2], submeshes=lods[2]['ids'], castShadows=False, triangles=stats[2]['tris']),
                   dict(fbx=files[3], screen=LOD_SCREEN[3], submeshes=[cross_id], castShadows=False, triangles=stats[3]['tris'])]
    man = dict(name=name, type='tree', group=GROUP, species='oak', form=P['form'], variant=variant,
               height=height, dbh=P['dbh'], crownRadius=crown_r, crownBase=round(sk.crown_base, 2),
               trunkRadius=round(sk.trunk_r13, 3),
               # у бурелома коллайдер — только пень (упавшая часть лежит без него)
               colliderHeight=(round(min(height, P['broken_at'] + 0.2), 2) if P.get('broken_at') else height),
               lods=lod_entries, materials=materials,
               nr=None)
    with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(man, f, indent=1)
    kinds = {}
    for b in sk.branches:
        kinds[b.kind] = kinds.get(b.kind, 0) + 1
    st = dict(name=name, height=height, target_H=P['H'], trunk_r13=sk.trunk_r13, crown_base=sk.crown_base,
              crown_extent=[list(map(float, lo)), list(map(float, hi))], crown_r=crown_r, lods=stats,
              branches=kinds, filled=sk.filled, l2_spacing=sk.l2_spacing, subcrowns=len(sk.crown.cl),
              clump_scale_history=hist, cov_lod1=cov1, z_top_cross=z_top, kind_tris=tree.kind_tris)
    with open(os.path.join(folder, 'stats.json'), 'w', encoding='utf-8') as f:
        json.dump(st, f, indent=1)
    qa(folder, man, stats, P)
    print('ASSET', name, json.dumps([dict(t=s['tris'], b=s.get('bark'), c=s.get('cards')) for s in stats]), 'H', height,
          'kinds', kinds, 'filled', sk.filled)
    return folder


def qa(folder, man, stats, P):
    """Числовые проверки (qa.json): бюджеты, высота, низ ствола, цвет вершин карточек, маски, файлы."""
    import numpy as np
    res = {}
    t = [s['tris'] for s in stats]
    res['tris'] = t
    res['budget_lod0'] = t[0] <= 12000
    res['budget_lod1_ratio'] = round(t[1] / t[0], 3)
    res['budget_lod2_ratio'] = round(t[2] / t[0], 3)
    res['cross_le_24'] = t[3] <= 24
    res['decreasing'] = t[0] > t[1] > t[2] > t[3]
    res['height_vs_lod0_top'] = abs(man['height'] - stats[0]['zmax']) / man['height'] <= 0.02
    res['zmin'] = round(stats[0]['zmin'], 3)
    res['zmin_ok'] = -0.45 <= stats[0]['zmin'] <= -0.35
    files_ok = True
    for lod in man['lods']:
        files_ok &= os.path.exists(os.path.join(folder, lod['fbx']))
    for m in man['materials']:
        for k in ('albedo', 'normal', 'mask'):
            if m.get(k):
                files_ok &= os.path.exists(os.path.join(folder, m[k]))
    res['files_exist'] = bool(files_ok)
    ids = set(m['id'] for m in man['materials'])
    res['ids_resolve'] = all(s in ids for lod in man['lods'] for s in lod['submeshes'])
    for nm in [m['mask'] for m in man['materials'] if m.get('mask')]:
        img = bpy.data.images.load(os.path.join(folder, nm), check_existing=False)
        px = np.array(img.pixels[:], np.float32).reshape(-1, 4)
        res['maskR0_' + nm] = bool(px[:, 0].max() < 1 / 255 + 1e-6)
        bpy.data.images.remove(img)
    with open(os.path.join(folder, 'qa.json'), 'w', encoding='utf-8') as f:
        json.dump(res, f, indent=1)
    print('QA', json.dumps(res))


def main():
    args = eg.script_args()
    out_root = args[0] if args else os.path.join(os.environ.get('TEMP', '.'), 'oak_out')
    names = [a for a in args[1:] if a in PRESETS] or list(PRESETS)
    tex_dir = os.path.join(out_root, '_tex')
    meta, cmeta = make_textures(tex_dir, '--textures' in args)
    for n in names:
        build_asset(n, out_root, tex_dir, meta, cmeta)


if __name__ == '__main__':
    main()
