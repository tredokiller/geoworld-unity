# Стерня и всходы на поле — альфа-карточками (стандарт игровой травы), вместо стеблей-полигонов field_gen.py.
# Почему: одиночный стебель в 3 мм шириной в игре уже с метра тоньше пикселя и пропадает, а лежащие соломины-полигоны
# читались плоскими оранжевыми «досками». Здесь стебли и солома живут в текстуре.
#  - Текстуры (1.2 мм на тексель; имена field_cards_* не пересекаются с field_set_* и field_atlas_* других префабов группы
#    Field) — у каждого материала своя: импортёр сохраняет покрытие альфы в мипах одним множителем на всю текстуру, и в
#    общей текстуре редкая солома (или всходы) редела вдали, пока густые полосы стеблей густели. Поэтому у ассета два
#    подмеша: вертикальные карточки на текстуре полос (field_cards_<набор>_strips/_side 2048×1024 или 2048×512) и
#    подстилка на своей плитке 1024² (field_cards_<набор>_mulch/_top).
#  - Полосы «вид сбоку» 2.46 м — срез стерни глубиной 0.12–0.13 м (≈ 65 стеблей на метр полосы: карточек в пятне
#    много, и при срезе 0.28 м стерня с 0.8 м закрывала 90 % земли — сплошная щётка, а не стерня): полые стебли 3–5 мм с косым срезом, кущение по 1–5, узлы,
#    остатки листьев, 5–9 % сломаны или наклонены, срез жатки ровный (σ ≈ 1 см), тёмная полоса у земли (2–3 см свежей,
#    4 см у октябрьской, с плесенью). Полоса нарезана на участки 0.30–0.45 м, к краю участка стебли редеют до нуля за
#    4 см — карточка берёт целые участки и кончается мягко, без стены стеблей. Под каждой полосой 22–32 строки повторяют
#    её нижнюю строку: подземная «юбка» карточек берёт их, и в дальних мипах низ полосы не выцветает.
#  - Плитки «вид сверху»: измельчённая комбайном солома (медиана 3 см, до 12 см; прямые куски с одним изломом у узла,
#    расщеплённые плоские с косыми и разлохмаченными концами, ширина ±30 % по длине, 30 % — вдоль одного направления,
#    8–10 % тёмных), полова и обрывки листьев — покрытие 0.40 (свежая) и 0.42 (октябрь: стебли сверху закрывают меньше)
#    внутри пятна, почти без кучности;
#    всходы — гнёзда по 1–5 растений по сетке с джиттером (покрытие ~0.22, без рядов: одиночные листья в дальних мипах
#    при билинейной выборке уходят под порог, гнёзда остаются).
#  - Запекание — Cycles, орто, эмиссия (альбедо), нормаль в пространстве камеры (касательная карточки, OpenGL, Y+ как в
#    Unity), AO (G маски), гладкость (A маски); цвет растекается за край альфы (push-pull), чтобы в мипах не было каймы.
#  - Пятно стерни: 8 вертикальных карточек 0.6–1.4 м в трёх семействах через ~60° (±10°), параллельные в 0.20–0.25 м,
#    4 звена с изломом 3–9°, по высоте один ряд и подземная юбка до −6 см (на склоне низ карточки не висит в воздухе),
#    плюс подстилка — 12-угольник 0.74 м по углам (плитка растянута в 1.2 раза), куполом: 8–12 мм в центре, 3–5 мм по краю
#    (соседние экземпляры не лежат в одной плоскости — нет z-конфликта). 140 треугольников. Всходы: 16 низких карточек
#    0.4–0.6 м в трёх семействах в круге 0.72 м и такая же плитка сверху, 76 треугольников.
#    Ветер — альфа цвета вершин min(1, (h/0.2)^1.5)·0.6, у стерни × 0.2 (не больше 0.12: срезанная стерня жёсткая, а ветер
#    шейдера поворачивает всё пятно вокруг опоры), 0 у подстилки и юбки; нормали вершин — смесь «вверх» и грани.
# Ассеты (имена прежние — профиль игры продолжает работать): field_stubble_fresh_01/02 (свежая стерня, июль–август),
# field_stubble_aged_01 (октябрьская серо-бурая стерня с падалицей), field_sprouts_01 (всходы озимых без рядов).
# Предпросмотр (подкоманда preview) подражает игре: вид Standard, солнце ~40°, небо, без теней от растений, альфа-тест с
# порогом материала, мипы с сохранением покрытия по каждой текстуре (как mipMapsPreserveCoverage импортёра) и выбор мипа
# по расстоянию и углу (анизотропия 4), расстановка как у GroundDressing + Nature Renderer (ячейки 2 м, floor + доля),
# SizeVariation пятнами 6 м, дальность, погружение и (если задано в манифесте) прореживание вдали по манифесту.
# Свет по умолчанию (--light game): кадр из игры, пересчитанный без оттенка ветра материала (_WINDCOLORMIX_ON у старой
# стерни умножал цвет на ~(0.81, 0.70, 0.70) — он и делал солому оранжевой): солнце (1, 0.91, 0.79) × 1.72, небо
# (0.86, 0.93, 1.0) × 0.27. --light neutral — нейтральный дневной свет для проверки цвета; --wind-tint r,g,b — оттенок
# ветра (для сравнения старых ассетов с кадром из игры); --grey-card — серая (50 %) карточка для сверки света с игрой.
# Запуск:
#   blender.exe -b --factory-startup --python field_cards_gen.py -- atlas <папка> [области...]   запечь текстуры
#   blender.exe -b --factory-startup --python field_cards_gen.py -- assets <папка>       собрать ассеты (текстуры уже есть)
#   blender.exe -b --factory-startup --python field_cards_gen.py -- all <папка>
#   blender.exe -b --factory-startup --python field_cards_gen.py -- preview <out.png> --mix <папка ассета>:<экз/м²>,... [опции]
import argparse
import json
import math
import os
import random
import shutil
import sys
import zlib

import bpy
import numpy as np
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

TEXEL = 0.0012           # м на тексель (полосы и плитки одинаково)
ATLAS_NAME = 'field_cards'
UP = np.array([0.0, 0.0, 1.0])
STRAW_SIDES = 12         # подстилка — 12-угольник по содержимому плитки (меньше пустых фрагментов, чем у восьмиугольника)
STRAW_R = 0.74           # радиус углов, м
STRAW_SCALE = 1.2        # плитка растянута: тексель подстилки 1.44 мм
# наибольший радиус содержимого плитки (в её единицах): апофема 12-угольника / растяжение — солома не обрезается краем
TILE_R = STRAW_R * math.cos(math.pi / STRAW_SIDES) / STRAW_SCALE - 0.003

# Текстуры: у каждого материала своя (сохранение покрытия в мипах — один множитель на текстуру).
# Области: x, y — левый верхний угол в пикселях картинки (y вниз), w, h — размер, pad — строки повтора низа полосы.
TEXTURES = {
    'fresh_strips': dict(w=2048, h=1024, regions={
        'fresh_a': (0, 0, 2048, 256, 'strip', 32), 'fresh_b': (0, 256, 2048, 256, 'strip', 32),
        'fresh_c': (0, 512, 2048, 256, 'strip', 32), 'fresh_d': (0, 768, 2048, 256, 'strip', 32)}),
    'fresh_mulch': dict(w=1024, h=1024, regions={'mulch_fresh': (0, 0, 1024, 1024, 'tile', 0)}),
    'aged_strips': dict(w=2048, h=1024, regions={
        'aged_a': (0, 0, 2048, 256, 'strip', 32), 'aged_b': (0, 256, 2048, 256, 'strip', 32),
        'aged_c': (0, 512, 2048, 256, 'strip', 32), 'aged_low': (0, 768, 2048, 128, 'strip', 22),
        'aged_low_b': (0, 896, 2048, 128, 'strip', 22)}),
    'aged_mulch': dict(w=1024, h=1024, regions={'mulch_aged': (0, 0, 1024, 1024, 'tile', 0)}),
    'sprouts_side': dict(w=2048, h=512, regions={
        'sprout_side_a': (0, 0, 2048, 128, 'strip', 22), 'sprout_side_b': (0, 128, 2048, 128, 'strip', 22),
        'sprout_side_c': (0, 256, 2048, 128, 'strip', 22), 'sprout_side_d': (0, 384, 2048, 128, 'strip', 22)}),
    'sprouts_top': dict(w=1024, h=1024, regions={'sprout_top': (0, 0, 1024, 1024, 'tile', 0)}),
}
REGIONS = {name: dict(x=r[0], y=r[1], w=r[2], h=r[3], kind=r[4], pad=r[5], texture=tex, tex_w=t['w'], tex_h=t['h'])
           for tex, t in TEXTURES.items() for name, r in t['regions'].items()}


def lin(h):
    return np.array(eg.hex_rgb(h), np.float64)


def luminance(c):
    return float(0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2])


def jitter(c, rng, lum=(0.9, 1.08), grey=(0.0, 0.08)):
    """Разброс цвета: ±яркость и обесцвечивание (никогда не насыщаем)."""
    g = rng.uniform(*grey)
    c = c * (1 - g) + luminance(c) * g
    return c * rng.uniform(*lum)


def smoothstep(a, b, x):
    t = np.clip((np.asarray(x, np.float64) - a) / (b - a), 0.0, 1.0)
    return t * t * (3 - 2 * t)


class VNoise:
    """Значимый шум на сетке 256² со сглаженной интерполяцией (для кучности соломы)."""

    def __init__(self, seed, n=256):
        self.g = np.random.default_rng(seed).random((n, n))
        self.n = n

    def __call__(self, x, y):
        x = np.asarray(x, np.float64)
        y = np.asarray(y, np.float64)
        ix, iy = np.floor(x).astype(np.int64), np.floor(y).astype(np.int64)
        fx, fy = x - ix, y - iy
        fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
        n, g = self.n, self.g
        a, b = g[ix % n, iy % n], g[(ix + 1) % n, iy % n]
        c, d = g[ix % n, (iy + 1) % n], g[(ix + 1) % n, (iy + 1) % n]
        return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy

    def fbm(self, x, y, octaves=3):
        total, amp, norm = 0.0, 1.0, 0.0
        for k in range(octaves):
            total = total + amp * self(x * 2 ** k + 17.3 * k, y * 2 ** k + 5.1 * k)
            norm += amp
            amp *= 0.5
        return total / norm


# ------------------------------------------------------------------------------------------- геометрия для запекания

class Geo:
    """Меш для рендера: вершины, четырёх- и треугольники, цвет на вершину (RGB — альбедо линейно, A — гладкость)."""

    def __init__(self):
        self.V, self.C, self.Q, self.T = [], [], [], []
        self.n = 0

    def add(self, V, C, quads=None, tris=None):
        V = np.asarray(V, np.float64).reshape(-1, 3)
        C = np.asarray(C, np.float64).reshape(-1, 4)
        if quads is not None and len(quads):
            self.Q.append(np.asarray(quads, np.int64).reshape(-1, 4) + self.n)
        if tris is not None and len(tris):
            self.T.append(np.asarray(tris, np.int64).reshape(-1, 3) + self.n)
        self.V.append(V)
        self.C.append(C)
        self.n += len(V)

    def to_object(self, name, smooth=True):
        V = np.concatenate(self.V).astype(np.float32)
        C = np.concatenate(self.C).astype(np.float32)
        Q = np.concatenate(self.Q) if self.Q else np.zeros((0, 4), np.int64)
        T = np.concatenate(self.T) if self.T else np.zeros((0, 3), np.int64)
        loops = np.concatenate([Q.ravel(), T.ravel()]).astype(np.int32)
        sizes = np.concatenate([np.full(len(Q), 4), np.full(len(T), 3)]).astype(np.int32)
        starts = (np.cumsum(sizes) - sizes).astype(np.int32)
        me = bpy.data.meshes.new(name)
        me.vertices.add(len(V))
        me.vertices.foreach_set('co', V.ravel())
        me.loops.add(len(loops))
        me.loops.foreach_set('vertex_index', loops)
        me.polygons.add(len(sizes))
        me.polygons.foreach_set('loop_start', starts)
        me.update(calc_edges=True)
        me.validate(clean_customdata=False)
        if smooth:
            me.polygons.foreach_set('use_smooth', np.ones(len(me.polygons), bool))
        li = np.zeros(len(me.loops), np.int32)
        me.loops.foreach_get('vertex_index', li)
        attr = me.color_attributes.new('Col', 'FLOAT_COLOR', 'CORNER')
        attr.data.foreach_set('color', C[li].ravel())
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob


def frames(P):
    T = np.zeros_like(P)
    T[1:-1] = P[2:] - P[:-2]
    T[0] = P[1] - P[0]
    T[-1] = P[-1] - P[-2]
    T /= np.maximum(np.linalg.norm(T, axis=1, keepdims=True), 1e-9)
    ref = UP if abs(T.mean(0)[2]) < 0.8 else np.array([1.0, 0.0, 0.0])
    N1 = np.cross(T, ref)
    N1 /= np.maximum(np.linalg.norm(N1, axis=1, keepdims=True), 1e-9)
    N2 = np.cross(T, N1)
    return T, N1, N2


def tube(geo, P, R, C, sides=8, flatten=1.0, phase=0.0, cut=None, cap=None):
    """Трубка по осевой P (m,3), радиусы R (m,), цвет колец C (m,4). flatten — сплющивание по второй оси (расщеплённая
    соломина), cut=(tan, азимут) — косой срез верха, cap=(цвет торца, цвет полости) — полый торец с внутренней стенкой."""
    P = np.asarray(P, np.float64)
    R = np.asarray(R, np.float64)
    C = np.asarray(C, np.float64)
    m = len(P)
    T, N1, N2 = frames(P)
    th = phase + np.arange(sides) * (2 * math.pi / sides)
    cos, sin = np.cos(th), np.sin(th)
    off = cos[None, :, None] * N1[:, None, :] + flatten * sin[None, :, None] * N2[:, None, :]
    V = P[:, None, :] + R[:, None, None] * off
    slant = None
    if cut is not None:
        tan_a, phi = cut
        slant = tan_a * np.cos(th - phi)
        V[-1] += T[-1][None, :] * (slant * R[-1])[:, None]
    idx = np.arange(m * sides).reshape(m, sides)
    j1 = np.roll(np.arange(sides), -1)
    quads = np.stack([idx[:-1, :], idx[:-1, j1], idx[1:, j1], idx[1:, :]], -1).reshape(-1, 4)
    geo.add(V.reshape(-1, 3), np.repeat(C, sides, axis=0), quads)
    if cap is not None:
        ri = 0.72 * R[-1]
        top = P[-1][None, :] + ri * off[-1]
        if slant is not None:
            top = top + T[-1][None, :] * (slant * ri)[:, None]
        deep = top - T[-1][None, :] * 0.006
        ring = np.arange(sides)
        Vc = np.concatenate([V[-1], top, deep])
        Cc = np.concatenate([np.repeat([cap[0]], sides, 0), np.repeat([cap[0]], sides, 0), np.repeat([cap[1]], sides, 0)])
        o, t, d = ring, ring + sides, ring + 2 * sides
        q1 = np.stack([o, o[j1], t[j1], t], -1)
        q2 = np.stack([t, t[j1], d[j1], d], -1)
        geo.add(Vc, Cc, np.concatenate([q1, q2]))


def ribbon(geo, P, W, S, C, fold=0.0):
    """Лента (лист): осевая P (m,3), полуширина W (m,), направление поперёк S (m,3), цвет C (m,4); fold — подъём средней
    жилки в долях ширины (сгиб листа)."""
    P = np.asarray(P, np.float64)
    W = np.maximum(np.asarray(W, np.float64), 0.0002)
    S = np.asarray(S, np.float64)
    T = np.zeros_like(P)
    T[1:-1] = P[2:] - P[:-2]
    T[0] = P[1] - P[0]
    T[-1] = P[-1] - P[-2]
    N = np.cross(T, S)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    left = P - S * W[:, None]
    right = P + S * W[:, None]
    mid = P + N * (fold * W)[:, None]
    m = len(P)
    V = np.stack([left, mid, right], 1).reshape(-1, 3)
    idx = np.arange(m * 3).reshape(m, 3)
    quads = []
    for k in range(2):
        quads.append(np.stack([idx[:-1, k], idx[:-1, k + 1], idx[1:, k + 1], idx[1:, k]], -1))
    geo.add(V, np.repeat(C, 3, axis=0), np.concatenate(quads))


def rot(axis, angle):
    return np.array(Matrix.Rotation(angle, 3, Vector(axis)))


def unit(v):
    v = np.asarray(v, np.float64)
    return v / max(np.linalg.norm(v), 1e-9)


# ---------------------------------------------------------------------------------------------------- палитры

def rgba(c, a):
    return np.array([c[0], c[1], c[2], a])


PAL = {
    # свежая стерня (июль–август): тёмная полоса у земли (2–3 см: прибитые обломки, влагалища), серо-бурый низ, к середине
    # солома, светлый срез; разброс — только к серому (никогда не к оранжевому)
    'fresh': dict(band='#6E6250', band_h=0.028, base='#8A7A5C', low='#A5936A', mid='#C4AC72', top='#D4C28A',
                  node='#8A784F', cap='#D8CA98', hollow='#5A5040', leaf=('#9E9276', '#B4A886'), grey=(0.0, 0.08),
                  lum=(0.9, 1.06), smooth=0.45),
    # октябрьская: выветренная серо-бурая, темнее свежей, низ 4 см тёмный с плесенью
    'aged': dict(band='#4F4A42', band_h=0.04, base='#7E7563', low='#877D69', mid='#9A907A', top='#A39A86',
                 node='#665E50', cap='#AAA18D', hollow='#4A453C', leaf=('#81786A', '#938A79'), grey=(0.02, 0.12),
                 lum=(0.86, 1.05), smooth=0.22),
}
SPROUT = dict(base='#7A8A5C', lo='#4A6C38', hi='#5A7D42', smooth=0.4)
VOLUNTEER = dict(base='#74845A', lo='#4F6C35', hi='#5A7A3E', smooth=0.4)


def stalk_colours(z, h, pal, shade, rng, mould=0.0):
    """Цвет по высоте z (м) у стебля высотой h: тёмная полоса у земли (band_h), нижняя треть серо-бурая,
    середина — солома, верхние ~1 см светлеют к срезу."""
    band, base, low, mid, top = (lin(pal[k]) for k in ('band', 'base', 'low', 'mid', 'top'))
    bh = pal['band_h']
    out = []
    for zz in z:
        if zz < bh:
            c = band
        elif zz < bh + 0.012:
            c = band + (base - band) * (zz - bh) / 0.012
        elif zz < 0.4 * h:
            c = base + (low - base) * (zz - bh - 0.012) / max(0.4 * h - bh - 0.012, 1e-3)
        elif zz < 0.58 * h:
            t = float(smoothstep(0.0, 1.0, (zz - 0.4 * h) / (0.18 * h)))
            c = low + (mid - low) * t
        elif zz < h - 0.012:
            c = mid
        else:
            c = mid + (top - mid) * min(1.0, (zz - (h - 0.012)) / 0.012)
        c = c * shade[0]
        c = c * (1 - shade[1]) + luminance(c) * shade[1]
        if mould and rng.random() < mould:
            c = c * rng.uniform(0.68, 0.86)
        out.append(rgba(c, pal['smooth']))
    return np.array(out)


def stalk(geo, rng, base, h, r, pal, lean, lean_az, broken=None, leaf=False, mould=0.0, sides=8):
    """Полый срезанный стебель: основание base (уходит на 3 см под землю), высота h, радиус r, наклон lean (рад) по
    азимуту lean_az; два узла (утолщение, тёмная полоса); broken=(высота излома, угол, азимут) — верх переломлен у узла
    и клонится (лежащая часть не уходит под землю); leaf — остаток сухого листа у нижнего узла."""
    n1 = rng.uniform(0.035, 0.06)
    n2 = rng.uniform(0.10, 0.14)
    zs = [-0.03, 0.0, 0.012, pal['band_h'], pal['band_h'] + 0.012]
    for nz in (n1, n2):
        if nz < h - 0.02:
            zs += [nz - 0.003, nz, nz + 0.003]
    zs += [h * 0.4, h * 0.58, h * 0.75, h - 0.012, h]
    zs = sorted(set(round(z, 5) for z in zs if z <= h))
    zs = np.array(zs)
    dirh = np.array([math.cos(lean_az), math.sin(lean_az), 0.0])
    ang = lean * (0.6 + 0.4 * np.clip(zs / h, 0, 1))
    P = np.array(base)[None, :] + dirh[None, :] * (np.tan(ang) * np.maximum(zs, 0))[:, None] + UP[None, :] * zs[:, None]
    if broken is not None:
        zb, angle, az = broken
        k = int(np.argmin(np.abs(zs - zb)))
        pivot = P[k].copy()
        axis = np.cross(UP, [math.cos(az), math.sin(az), 0.0])
        R = rot(axis, angle)
        for i in range(k + 1, len(P)):
            P[i] = pivot + R @ (P[i] - pivot)
        P[:, 2] = np.maximum(P[:, 2], np.where(zs > zb, r * 0.9, -1.0))
    radii = np.full(len(zs), r)
    radii[zs < 0.015] *= 1.08
    for nz in (n1, n2):
        radii[np.abs(zs - nz) < 1e-4] *= 1.13
    radii[-1] *= 0.97
    shade = (rng.uniform(*pal['lum']), rng.uniform(*pal['grey']))
    C = stalk_colours(zs, h, pal, shade, rng, mould)
    node_c = lin(pal['node']) * shade[0]
    for i, z in enumerate(zs):
        for nz in (n1, n2):
            if abs(z - nz) < 1e-4:
                C[i, :3] = node_c
    cap_c = rgba(lin(pal['cap']) * shade[0], pal['smooth'])
    hollow = rgba(lin(pal['hollow']), 0.1)
    tan_a = math.tan(math.radians(rng.uniform(5, 40)))
    tube(geo, P, radii, C, sides=sides, phase=rng.uniform(0, 1), cut=(tan_a, rng.uniform(0, math.tau)), cap=(cap_c, hollow))
    if leaf and n1 < h - 0.02:
        dried_leaf(geo, rng, P[np.argmin(np.abs(zs - n1))], r, pal)
    return P


def dried_leaf(geo, rng, at, r, pal):
    """Остаток сухого листа: влагалище 1–2 см вдоль стебля, потом лист отходит на 30–70° и свисает, скручиваясь."""
    az = rng.uniform(0, math.tau)
    out = np.array([math.cos(az), math.sin(az), 0.0])
    side = np.cross(UP, out)
    length = rng.uniform(0.04, 0.09)
    sheath = rng.uniform(0.008, 0.02)
    n = 6
    pts, widths, sides = [], [], []
    p = np.array(at) + out * r * 1.2
    d = UP.copy()
    curl = rng.uniform(0.6, 1.4)
    for i in range(n):
        t = i / (n - 1)
        pts.append(p.copy())
        widths.append(rng.uniform(0.0018, 0.0026) * (1 - 0.85 * t ** 2))
        twist = curl * t * 1.5
        s = side * math.cos(twist) + UP * math.sin(twist) * 0.3
        sides.append(unit(s))
        seg = (sheath if i == 0 else (length - sheath) / (n - 2))
        if i == 0:
            d = unit(UP * math.cos(math.radians(rng.uniform(30, 70))) + out * math.sin(math.radians(50)))
        else:
            d = unit(d - UP * rng.uniform(0.15, 0.45))
        p = p + d * seg
        p[2] = max(p[2], 0.003)
    a, b = lin(pal['leaf'][0]), lin(pal['leaf'][1])
    C = np.array([rgba((a + (b - a) * (i / (n - 1))) * rng.uniform(0.9, 1.05), 0.3) for i in range(n)])
    ribbon(geo, np.array(pts), np.array(widths), np.array(sides), C, fold=rng.uniform(0.2, 0.5))


def seedling(geo, rng, base, palette, leaves=(2, 3), length=(0.05, 0.12), width=(0.0015, 0.0025), first_up=True, upright=False):
    """Всход пшеницы: 2–3 узких листа 5–12 см, двурядно (следующий — с другой стороны), первый прямее, остальные дугой
    наружу и вниз; бледное основание (колеоптиль), лист от тёмного сизого к чуть светлее. width — полуширина листа."""
    n_leaves = rng.randint(*leaves)
    az0 = rng.uniform(0, math.tau)
    lo, hi, pale = lin(palette['lo']), lin(palette['hi']), lin(palette['base'])
    tone = rng.random() ** 1.6  # чаще тёмная сизая зелень
    body = lo + (hi - lo) * tone
    for k in range(n_leaves):
        az = az0 + (math.pi if k % 2 else 0.0) + rng.uniform(-0.35, 0.35)
        out = np.array([math.cos(az), math.sin(az), 0.0])
        side = np.cross(UP, out)
        L = rng.uniform(*length) * (1.0 if k else rng.uniform(0.7, 1.0))
        rise = math.radians(rng.uniform(70, 85) if (k == 0 and first_up) else rng.uniform(45, 75))
        droop = math.radians(rng.uniform(30, 80) if k else rng.uniform(5, 35))
        if upright:
            # для плитки «вид сверху»: стоячие листья сверху — короткие штрихи 2–4 см, а не лежащие на земле ленты
            rise = math.radians(rng.uniform(68, 86))
            droop = math.radians(rng.uniform(5, 40))
        n = 6
        pts, ws, ss, cs = [], [], [], []
        p = np.array(base, np.float64) + np.array([rng.uniform(-0.002, 0.002), rng.uniform(-0.002, 0.002), 0.0])
        for i in range(n):
            t = i / (n - 1)
            pts.append(p.copy())
            ws.append(rng.uniform(*width) * (1.0 if t < 0.6 else max(0.08, 1 - (t - 0.6) / 0.4)))
            twist = rng.uniform(-0.3, 0.3) * t
            ss.append(unit(side * math.cos(twist) + UP * math.sin(twist)))
            c = pale + (body - pale) * min(1.0, t / 0.18)
            c = c * rng.uniform(0.95, 1.04) * (1.0 + 0.06 * t)
            cs.append(rgba(c, palette['smooth']))
            angle = rise - droop * t ** 1.3
            d = out * math.cos(angle) + UP * math.sin(angle)
            p = p + d * (L / (n - 1))
            p[2] = max(p[2], 0.002)
        ribbon(geo, np.array(pts), np.array(ws), np.array(ss), np.array(cs), fold=rng.uniform(0.3, 0.6))


# -------------------------------------------------------------------------------------------------- сцены атласа

STRIPS = {
    # plants — растений на м² (кущение 1–5 стеблей, в среднем ~2.4 → ~550 стеблей/м²), depth — глубина среза, м
    # (8 карточек по ~1.2 м на пятно, 3 пятна на м²: срез 0.12 м даёт с 0.8 м ~60–70 % закрытой стеблями земли);
    # h = (средняя, σ, мин, макс) высота среза жатки; у свежей σ ≈ 1 см — ровная щётка; seg — длины участков полосы
    'fresh_a': dict(pal='fresh', plants=235, depth=0.12, h=(0.156, 0.010, 0.13, 0.185), r=(0.0015, 0.0025),
                    broken=0.06, leaf=0.35, lean=(0.0, 0.12), short=0.03),
    'fresh_c': dict(pal='fresh', plants=230, depth=0.12, h=(0.162, 0.009, 0.135, 0.19), r=(0.0015, 0.0025),
                    broken=0.05, leaf=0.3, lean=(0.0, 0.10), short=0.03),
    'fresh_b': dict(pal='fresh', plants=235, depth=0.12, h=(0.144, 0.011, 0.115, 0.175), r=(0.0015, 0.0025),
                    broken=0.08, leaf=0.45, lean=(0.0, 0.15), short=0.03),
    'fresh_d': dict(pal='fresh', plants=240, depth=0.12, h=(0.138, 0.012, 0.11, 0.17), r=(0.0015, 0.0025),
                    broken=0.09, leaf=0.5, lean=(0.0, 0.16), short=0.03),
    # октябрь: стерня полегла и поломана местами, плесень, падалица; на полосу 2.46 м — 50–80 лежащих обломков и
    # 16–26 растений падалицы
    'aged_a': dict(pal='aged', plants=215, depth=0.13, h=(0.14, 0.015, 0.09, 0.18), r=(0.0014, 0.0024),
                   broken=0.14, leaf=0.5, lean=(0.03, 0.25), short=0.05, mould=0.25, volunteers=22, lodged=60),
    'aged_b': dict(pal='aged', plants=210, depth=0.13, h=(0.148, 0.014, 0.1, 0.19), r=(0.0014, 0.0024),
                   broken=0.12, leaf=0.45, lean=(0.03, 0.22), short=0.05, mould=0.25, volunteers=18, lodged=50),
    'aged_c': dict(pal='aged', plants=205, depth=0.13, h=(0.132, 0.016, 0.08, 0.175), r=(0.0014, 0.0024),
                   broken=0.16, leaf=0.5, lean=(0.04, 0.28), short=0.06, mould=0.25, volunteers=26, lodged=70),
    'aged_low': dict(pal='aged', plants=185, depth=0.12, h=(0.085, 0.014, 0.05, 0.115), r=(0.0014, 0.0024),
                     broken=0.3, leaf=0.4, lean=(0.05, 0.35), short=0.1, mould=0.25, volunteers=18, lodged=70),
    'aged_low_b': dict(pal='aged', plants=180, depth=0.12, h=(0.08, 0.014, 0.05, 0.11), r=(0.0014, 0.0024),
                       broken=0.32, leaf=0.4, lean=(0.05, 0.4), short=0.1, mould=0.25, volunteers=16, lodged=80),
}
STRIP_SEG = (0.30, 0.45)    # участки полосы стерни, м
SPROUT_SEG = (0.20, 0.30)   # участки полосы всходов
TAPER = 0.04                # к краю участка стебли редеют до нуля за 4 см
EDGE_CLEAR = 0.012          # и не ближе 1.2 см к краю (кущение расходится на ±1 см)


def tiller_count(rng):
    return rng.choices([1, 2, 3, 4, 5], weights=[0.22, 0.3, 0.27, 0.14, 0.07])[0]


def segment_edges(rng, width, seg):
    """Границы участков полосы (м): 0, …, width; последний участок не короче 0.6 от наименьшего."""
    edges = [0.0]
    while width - edges[-1] > seg[1]:
        edges.append(edges[-1] + rng.uniform(*seg))
    if width - edges[-1] < seg[0] * 0.6 and len(edges) > 1:
        edges.pop()
    edges.append(width)
    return edges


def edge_distance(x, edges):
    i = max(0, min(len(edges) - 2, int(np.searchsorted(edges, x)) - 1))
    return min(x - edges[i], edges[i + 1] - x)


def edge_density(x, edges):
    return float(smoothstep(EDGE_CLEAR, EDGE_CLEAR + TAPER, edge_distance(x, edges)))


def build_strip(name, cfg, width):
    """Срез стерни: x ∈ [0, width], глубина y ∈ [0, depth]; камера смотрит вдоль +Y."""
    rng = random.Random(zlib.crc32(name.encode()))
    pal = PAL[cfg['pal']]
    geo = Geo()
    hnoise = VNoise(zlib.crc32((name + 'h').encode()))
    edges = segment_edges(rng, width, STRIP_SEG)
    n_plants = int(cfg['plants'] * width * cfg['depth'] * 1.12)  # +12 %: на сужения к краям участков
    stalks = 0
    mean, sd, lo, hi = cfg['h']
    for i in range(n_plants):
        # вдоль полосы — с джиттером по ячейкам (без проплешин внутри участка), по глубине — случайно
        cx, cy = (i + rng.random()) / n_plants * width, rng.uniform(0, cfg['depth'])
        if rng.random() > edge_density(cx, edges):
            continue
        near = min(1.0, edge_distance(cx, edges) / 0.1)  # у края участка — без сломанных и наклонённых наружу
        # высота среза жаткой: ровная, медленно гуляет по полю
        h0 = mean + sd * rng.gauss(0, 0.8) + 0.5 * sd * (hnoise(cx / 0.35, cy / 0.35) - 0.5) * 2
        for k in range(tiller_count(rng)):
            ang = rng.uniform(0, math.tau)
            d = rng.uniform(0.003, 0.010) if k else 0.0
            base = (cx + d * math.cos(ang), cy + d * math.sin(ang), 0.0)
            h = min(hi, max(lo, h0 + rng.gauss(0, 0.004)))
            if rng.random() < cfg['short']:
                h = rng.uniform(0.04, max(0.05, lo))  # подгон, не дотянувшийся до жатки
            lean = rng.uniform(*cfg['lean']) * near
            lean_az = ang if k else rng.uniform(0, math.tau)
            broken = None
            if near >= 1.0 and rng.random() < cfg['broken'] and h > 0.07:
                broken = (rng.uniform(0.035, min(0.12, h - 0.02)), math.radians(rng.uniform(25, 85)), rng.uniform(0, math.tau))
            stalk(geo, rng, base, h, rng.uniform(*cfg['r']), pal, lean, lean_az, broken, rng.random() < cfg['leaf'],
                  cfg.get('mould', 0.0))
            stalks += 1
    for _ in range(cfg.get('lodged', 0)):
        # лежащие у земли обломки стеблей (видны снизу полосы), не у краёв участков
        for _try in range(20):
            x0 = rng.uniform(0, width)
            if edge_distance(x0, edges) > 0.09:
                break
        y0 = rng.uniform(0, cfg['depth'])
        az = rng.uniform(0, math.tau)
        L = rng.uniform(0.05, 0.12)
        d = np.array([math.cos(az) * 0.5, math.sin(az), math.sin(math.radians(rng.uniform(2, 15)))])
        P = np.array([x0, y0, 0.003]) + np.outer(np.linspace(0, 1, 4), unit(d) * L)
        C = np.array([rgba(lin(pal['low']) * rng.uniform(0.8, 1.0), pal['smooth'])] * 4)
        tube(geo, P, np.full(4, rng.uniform(0.0014, 0.0022)), C, sides=6, flatten=0.8)
    for _ in range(cfg.get('volunteers', 0)):
        for _try in range(20):
            x0 = rng.uniform(0.05, width - 0.05)
            if edge_distance(x0, edges) > 0.1:
                break
        seedling(geo, rng, (x0, rng.uniform(0.0, cfg['depth']), 0.0), VOLUNTEER,
                 leaves=(3, 5), length=(0.08, 0.15), width=(0.002, 0.003))
    return geo, dict(stalks=stalks, plants=n_plants, edges=edges)


def build_sprout_strip(name, width, depth=0.12, plants=380):
    rng = random.Random(zlib.crc32(name.encode()))
    geo = Geo()
    edges = segment_edges(rng, width, SPROUT_SEG)
    n = int(plants * width * depth * 1.5)
    placed = 0
    i = 0
    while i < n:
        x, y = (i + rng.random()) / n * width, rng.uniform(0, depth)
        group = rng.choices([1, 2, 3], weights=[0.4, 0.4, 0.2])[0]
        if rng.random() <= edge_density(x, edges) * (0.75 + 0.25 * rng.random()):
            for _ in range(group):
                seedling(geo, rng, (x + rng.gauss(0, 0.008), y + rng.gauss(0, 0.008), 0.0), SPROUT)
                placed += 1
        i += group
    return geo, dict(plants=placed, edges=edges)


# плитки «вид сверху»: край пятна неровный, плотность спадает только в крайних 15 %

def edge_radius(x, y, phases, inset=0.0):
    """Неровный край пятна: радиус по углу (гармоники 2, 3, 5, ±10 %), не больше TILE_R − inset."""
    th = math.atan2(y, x)
    k = 0.55 * math.sin(2 * th + phases[0]) + 0.3 * math.sin(3 * th + phases[1]) + 0.15 * math.sin(5 * th + phases[2])
    return (TILE_R - inset) * (1.0 + 0.1 * k) / 1.1


def radial_weight(x, y, phases, inset=0.0):
    """Плотность по радиусу: ровно внутри, мягкий спад только в крайних 15 % (неровного) края."""
    return 1.0 - float(smoothstep(0.85, 1.0, math.hypot(x, y) / edge_radius(x, y, phases, inset)))


def tile_density(x, y, clump, clump_scale, clump_k, phases=(0.0, 1.0, 2.0)):
    radial = radial_weight(x, y, phases)
    cl = float(smoothstep(0.38, 0.72, clump.fbm(x / clump_scale, y / clump_scale)))
    return radial * ((1 - clump_k) + clump_k * 1.6 * cl)


def width_profile(rng, n):
    """Ширина вдоль куска соломы: ±30 % плавной волной."""
    t = np.linspace(0, 1, n)
    f1, f2 = rng.uniform(0.5, 1.5), rng.uniform(2.0, 4.0)
    p1, p2 = rng.uniform(0, math.tau), rng.uniform(0, math.tau)
    return np.clip(1 + 0.3 * (0.6 * np.sin(math.tau * f1 * t + p1) + 0.4 * np.sin(math.tau * f2 * t + p2)), 0.65, 1.32)


def straw_axis(rng, x, y, az, length, kink_p):
    """Осевая куска: прямая, иногда с одним резким изломом 25–60° у узла; возвращает точки (центр в x, y), долю излома."""
    d0 = np.array([math.cos(az), math.sin(az), 0.0])
    n = int(np.clip(length / 0.006, 3, 10)) + 1
    t = list(np.linspace(0, 1, n))
    tk = None
    if length > 0.03 and rng.random() < kink_p:
        tk = rng.uniform(0.3, 0.7)
        t = sorted(set([round(v, 4) for v in t] + [round(tk, 4)]))
        d1 = rot(UP, math.radians(rng.uniform(25, 60)) * rng.choice([-1, 1])) @ d0
    P = []
    for ti in t:
        if tk is None or ti <= tk:
            P.append(d0 * ti * length)
        else:
            P.append(d0 * tk * length + d1 * (ti - tk) * length)
    P = np.array(P)
    P += np.array([x, y, 0.0]) - P.mean(0)
    return P, np.array(t), tk


def flat_straw(geo, rng, P, t, tk, width, colour, smooth, layer):
    """Расщеплённая плоская соломина: лента с переменной шириной, косыми срезами концов, иногда разлохмаченными концами
    (2–3 волокна по 1–1.6 мм)."""
    m = len(P)
    T = np.zeros_like(P)
    T[1:-1] = P[2:] - P[:-2]
    T[0] = P[1] - P[0]
    T[-1] = P[-1] - P[-2]
    T /= np.maximum(np.linalg.norm(T, axis=1, keepdims=True), 1e-9)
    S = np.cross(UP, T)
    S /= np.maximum(np.linalg.norm(S, axis=1, keepdims=True), 1e-9)
    W = 0.5 * width * width_profile(rng, m)
    if tk is not None:
        k = int(np.argmin(np.abs(t - tk)))
        cosh = max(0.5, float(np.dot(unit(P[k] - P[k - 1]), unit(P[k + 1] - P[k])))) if 0 < k < m - 1 else 1.0
        W[k] /= math.sqrt((1 + cosh) / 2)  # ширина на изломе (угол среза пополам)
    z = np.full(m, layer)
    L = P - S * W[:, None]
    R = P + S * W[:, None]
    M = P + UP * (0.12 * W)[:, None]
    for end, sgn in ((0, -1), (m - 1, 1)):
        delta = W[end] * math.tan(math.radians(rng.uniform(20, 70))) * rng.choice([-1, 1])
        L[end] += T[end] * delta * 0.5
        R[end] -= T[end] * delta * 0.5
    L[:, 2] = z
    R[:, 2] = z
    M[:, 2] = z + 0.12 * W
    V = np.stack([L, M, R], 1).reshape(-1, 3)
    idx = np.arange(m * 3).reshape(m, 3)
    quads = [np.stack([idx[:-1, j], idx[:-1, j + 1], idx[1:, j + 1], idx[1:, j]], -1) for j in range(2)]
    cols = []
    for i in range(m):
        c = colour * rng.uniform(0.95, 1.04)
        if tk is not None and abs(t[i] - tk) < 1e-4:
            c = c * 0.8  # узел
        cols.append(rgba(c, smooth))
    geo.add(V, np.repeat(np.array(cols), 3, axis=0), np.concatenate(quads))
    ends = [e for e in (0, m - 1) if rng.random() < 0.45]
    for end in ends:
        out = -T[0] if end == 0 else T[-1]
        for _ in range(rng.randint(2, 3)):
            start = P[end] + S[end] * rng.uniform(-0.7, 0.7) * W[end]
            d = rot(UP, math.radians(rng.uniform(2, 12)) * rng.choice([-1, 1])) @ out
            fl = rng.uniform(0.006, 0.02)
            pts = np.array([start + d * fl * s for s in (0.0, 0.5, 1.0)])
            pts[:, 2] = layer + 0.0002
            hw = rng.uniform(0.0005, 0.0008)
            side = unit(np.cross(UP, d))
            ribbon(geo, pts, np.array([hw, hw * 0.8, hw * 0.35]), np.repeat([side], 3, 0),
                   np.array([rgba(colour * rng.uniform(1.0, 1.08), smooth)] * 3), fold=0.0)


def whole_straw(geo, rng, P, t, tk, width, colour, smooth, layer):
    """Целая соломина-трубка (слегка сплющена), узел на изломе тёмный."""
    R = 0.5 * width * width_profile(rng, len(P))
    flatten = rng.uniform(0.7, 0.9)
    P = P.copy()
    P[:, 2] = layer + 0.5 * width * flatten
    C = []
    for i in range(len(P)):
        c = colour * rng.uniform(0.94, 1.05)
        if tk is not None and abs(t[i] - tk) < 1e-4:
            c = c * 0.78
        C.append(rgba(c, smooth))
    tube(geo, P, R, np.array(C), sides=6, flatten=flatten, phase=math.pi / 2)


def husk(geo, rng, x, y, colour, smooth, layer, size=1.0):
    """Чешуя колоса (полова): лодочка 6–10 мм × 2.5–3.5 мм (× size)."""
    az = rng.uniform(0, math.tau)
    L = rng.uniform(0.006, 0.010) * size
    w = rng.uniform(0.0025, 0.0035) * size
    d = np.array([math.cos(az), math.sin(az), 0.0])
    P = np.array([x, y, layer])[None, :] + np.outer(np.array([-0.5, -0.3, 0.1, 0.5]), d * L)
    P[:, 2] = layer + w * 0.2
    R = np.array([0.15, 0.5, 0.5, 0.12]) * w
    C = np.array([rgba(colour * rng.uniform(0.95, 1.06), smooth)] * 4)
    tube(geo, P, R, C, sides=6, flatten=0.35, phase=math.pi / 2)


def leaf_bit(geo, rng, x, y, colour, layer):
    """Обрывок сухого листа: лента 2–5 см × 3–6 мм, скрученная."""
    az = rng.uniform(0, math.tau)
    L = rng.uniform(0.02, 0.05)
    hw = rng.uniform(0.0015, 0.003)
    n = 5
    d = np.array([math.cos(az), math.sin(az), 0.0])
    side = np.cross(UP, d)
    curve = rng.uniform(-0.6, 0.6)
    pts, ss = [], []
    p = np.array([x, y, layer + 0.0006]) - d * L * 0.5
    for i in range(n):
        t = i / (n - 1)
        pts.append(p.copy())
        tw = rng.uniform(-0.5, 0.5) * t
        ss.append(unit(side * math.cos(tw) + UP * math.sin(tw)))
        d = rot(UP, curve / n) @ d
        p = p + d * (L / (n - 1))
    C = np.array([rgba(colour * rng.uniform(0.9, 1.06), 0.25)] * n)
    ribbon(geo, np.array(pts), np.full(n, hw), np.array(ss), C, fold=0.25)


MULCH = {
    # cover — цель покрытия внутри пятна (число кусков подбирается пробным рендером); покрытие набирается мелочью
    # (полова 3–4 на кусок, размер 1–1.6; медиана куска 2.8 см), а не длинными палками; кучность слабая (0.35 на 5 см) —
    # без пустых карманов в 15–20 см. 30 % кусков вдоль одного направления (валок), 8–10 % тёмных.
    'mulch_fresh': dict(cover=0.40, pieces=1600, length=0.028, husk_ratio=3.5, husk_size=(1.0, 1.6), leaf_ratio=0.3,
                        long=3, clump_scale=0.05, clump_k=0.35, kink=0.25, aligned=0.3, dark=0.09,
                        split=('#CBB680', '#C6B07A'), whole=('#BCA36C', '#B59D68'), under='#9C8962', dark_c='#9C8C68',
                        husk=('#CDBF95', '#C8B98C'), leaf=('#ADA083', '#B8AC8E'), smooth=0.35, volunteers=0),
    'mulch_aged': dict(cover=0.42, pieces=1400, length=0.026, husk_ratio=3.0, husk_size=(1.0, 1.5), leaf_ratio=0.3,
                       long=3, clump_scale=0.05, clump_k=0.35, kink=0.3, aligned=0.3, dark=0.10,
                       split=('#A39A86', '#9C927E'), whole=('#958B76', '#8C826E'), under='#6E665A', dark_c='#5E584D',
                       husk=('#A69D88', '#9C937F'), leaf=('#857C6B', '#7A7262'), smooth=0.2, volunteers=30),
}


def build_mulch(name, cfg, pieces):
    rng = random.Random(zlib.crc32(name.encode()))
    clump = VNoise(zlib.crc32((name + 'c').encode()))
    geo = Geo()
    phases = [rng.uniform(0, math.tau) for _ in range(3)]
    swath = rng.uniform(0, math.pi)  # направление валка
    made = 0

    def sample():
        for _ in range(200):
            x, y = rng.uniform(-TILE_R, TILE_R), rng.uniform(-TILE_R, TILE_R)
            if rng.random() * 1.25 < tile_density(x, y, clump, cfg['clump_scale'], cfg['clump_k'], phases):
                return x, y
        return None

    for _ in range(pieces):
        xy = sample()
        if xy is None:
            continue
        x, y = xy
        length = min(0.12, max(0.008, cfg['length'] * math.exp(rng.gauss(0, 0.6))))
        if math.hypot(x, y) + length * 0.5 > TILE_R:
            continue
        if rng.random() < cfg['aligned']:
            az = swath + math.radians(rng.uniform(-20, 20)) + (math.pi if rng.random() < 0.5 else 0.0)
        else:
            az = rng.uniform(0, math.tau)
        layer = rng.uniform(0.0004, 0.006)
        r = rng.random()
        flat = True
        if r < cfg['dark']:
            colour, flat = lin(cfg['dark_c']) * rng.uniform(0.9, 1.1), rng.random() < 0.5
        elif r < cfg['dark'] + 0.52:
            colour = lin(rng.choice(cfg['split']))
        elif r < cfg['dark'] + 0.80:
            colour, flat = lin(rng.choice(cfg['whole'])), False
        else:
            colour = lin(cfg['under'])
            layer *= 0.3
        colour = jitter(colour, rng, (0.94, 1.05), (0.0, 0.05))
        P, t, tk = straw_axis(rng, x, y, az, length, cfg['kink'])
        if flat:
            # расщеплённая комбайном солома — плоская лента 3.5–7 мм
            flat_straw(geo, rng, P, t, tk, rng.uniform(0.0035, 0.007), colour, cfg['smooth'], layer)
        else:
            # целая — трубка 2.5–4.5 мм
            whole_straw(geo, rng, P, t, tk, rng.uniform(0.0025, 0.0045), colour, cfg['smooth'], layer)
        made += 1
        hr = cfg['husk_ratio']
        for _ in range(int(hr) + (1 if rng.random() < hr - int(hr) else 0)):
            hx, hy = x + rng.gauss(0, 0.04), y + rng.gauss(0, 0.04)
            if math.hypot(hx, hy) < TILE_R - 0.01 and rng.random() < radial_weight(hx, hy, phases):
                husk(geo, rng, hx, hy, jitter(lin(rng.choice(cfg['husk'])), rng, (0.95, 1.04), (0.0, 0.05)),
                     cfg['smooth'], rng.uniform(0.0004, 0.007), rng.uniform(*cfg['husk_size']))
        if rng.random() < cfg['leaf_ratio']:
            lx, ly = x + rng.gauss(0, 0.04), y + rng.gauss(0, 0.04)
            if math.hypot(lx, ly) < TILE_R - 0.04:
                leaf_bit(geo, rng, lx, ly, lin(rng.choice(cfg['leaf'])), rng.uniform(0.0004, 0.006))
    for _ in range(cfg['long']):
        xy = sample()
        if xy is None:
            continue
        L = rng.uniform(0.08, 0.12)
        if math.hypot(*xy) + L * 0.5 > TILE_R:
            continue
        P, t, tk = straw_axis(rng, xy[0], xy[1], rng.uniform(0, math.tau), L, 0.5)
        whole_straw(geo, rng, P, t, tk, rng.uniform(0.003, 0.0045), lin(cfg['whole'][0]) * rng.uniform(0.92, 1.03),
                    cfg['smooth'], rng.uniform(0.004, 0.008))
    for _ in range(cfg.get('volunteers', 0)):
        a, rr = rng.uniform(0, math.tau), TILE_R * 0.85 * math.sqrt(rng.random())
        seedling(geo, rng, (rr * math.cos(a), rr * math.sin(a), 0.004), VOLUNTEER, leaves=(3, 5), length=(0.06, 0.13),
                 width=(0.002, 0.003), upright=True)
    return geo, dict(pieces=made)


def build_sprout_tile(name, nests):
    """Всходы сверху: гнёзда по 1–5 растений по сетке с джиттером (ровная плотность, без рядов и без кучности по
    шуму), мягкий спад только в крайних 15 % неровного края. Листья сверху — штрихи 3–4.5 мм шириной."""
    rng = random.Random(zlib.crc32(name.encode()))
    geo = Geo()
    phases = [rng.uniform(0, math.tau) for _ in range(3)]
    side = 2 * TILE_R
    cell = side / math.sqrt(max(nests, 1))
    n = int(math.ceil(side / cell))
    placed = 0
    for i in range(n):
        for j in range(n):
            x = -TILE_R + (i + 0.5 + rng.uniform(-0.45, 0.45)) * cell
            y = -TILE_R + (j + 0.5 + rng.uniform(-0.45, 0.45)) * cell
            if math.hypot(x, y) > TILE_R - 0.03 or rng.random() > radial_weight(x, y, phases, 0.03):
                continue
            # гнездо 1–5 растений (~3–4 см): в дальних мипах с билинейной выборкой одиночный лист под порогом пропадает,
            # гнездо остаётся пятном
            for _ in range(rng.choices([1, 2, 3, 4, 5], weights=[0.1, 0.25, 0.3, 0.2, 0.15])[0]):
                seedling(geo, rng, (x + rng.gauss(0, 0.007), y + rng.gauss(0, 0.007), 0.0), SPROUT, upright=True,
                         width=(0.0016, 0.00225))
                placed += 1
    return geo, dict(plants=placed, nests=nests)


# ------------------------------------------------------------------------------------------------- запекание

def pass_material(kind):
    """Материалы проходов: albedo — цвет атрибута, smooth — его альфа, normal — нормаль в пространстве камеры, ao."""
    if kind == 'normal':
        return eg.view_normal_material('pass_normal')
    m = bpy.data.materials.new('pass_' + kind)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    em = nt.nodes.new('ShaderNodeEmission')
    nt.links.new(em.outputs['Emission'], out.inputs['Surface'])
    if kind in ('albedo', 'smooth'):
        col = nt.nodes.new('ShaderNodeVertexColor')
        col.layer_name = 'Col'
        nt.links.new(col.outputs['Color' if kind == 'albedo' else 'Alpha'], em.inputs['Color'])
    else:
        ao = nt.nodes.new('ShaderNodeAmbientOcclusion')
        ao.samples = 24
        ao.inputs['Distance'].default_value = 0.03 if kind == 'ao_strip' else 0.012
        nt.links.new(ao.outputs['AO'], em.inputs['Color'])
    return m


def read_exr(path):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)[::-1].copy()
    bpy.data.images.remove(img)
    return px


def render_passes(scene, objects, w, h, tmp, strip):
    """Рендер четырёх проходов области в EXR (линейно, без видового преобразования); возвращает словарь массивов."""
    out = {}
    eg.setup_card_render(scene, w, h, samples=16)
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.filter_size = 1.0
    scene.cycles.max_bounces = 0
    scene.cycles.transparent_max_bounces = 0
    for kind in ('albedo', 'smooth', 'normal', 'ao'):
        mat = pass_material('ao_strip' if (kind == 'ao' and strip) else ('ao_tile' if kind == 'ao' else kind))
        scene.cycles.samples = 48 if kind == 'ao' else 16
        path = os.path.join(tmp, f'{kind}.exr')
        eg.render_with_override(scene, objects, mat, path)
        out[kind] = read_exr(path)
    return out


def unpremultiply(px):
    a = px[..., 3:4]
    rgb = np.where(a > 1e-4, px[..., :3] / np.maximum(a, 1e-4), 0.0)
    return np.concatenate([rgb, a], -1)


def push_pull(values, weight):
    """Заполнение прозрачного: значения из соседей по пирамиде (push-pull); где вес > 0 — остаются свои."""
    c = values * weight[..., None]
    w = weight.copy()
    levels = []
    while min(c.shape[:2]) > 1:
        levels.append((c, w))
        h2, w2 = c.shape[0] // 2, c.shape[1] // 2
        c = c[:h2 * 2, :w2 * 2].reshape(h2, 2, w2, 2, -1).sum((1, 3))
        w = w[:h2 * 2, :w2 * 2].reshape(h2, 2, w2, 2).sum((1, 3))
    col = c / np.maximum(w, 1e-8)[..., None]
    for c_l, w_l in reversed(levels):
        up = np.repeat(np.repeat(col, 2, 0), 2, 1)
        if up.shape[0] < c_l.shape[0]:
            up = np.concatenate([up, up[-1:]], 0)
        if up.shape[1] < c_l.shape[1]:
            up = np.concatenate([up, up[:, -1:]], 1)
        up = up[:c_l.shape[0], :c_l.shape[1]]
        own = c_l / np.maximum(w_l, 1e-8)[..., None]
        t = np.clip(w_l, 0, 1)[..., None]
        col = own * t + up * (1 - t)
    return np.where(weight[..., None] > 0, values, col)


def setup_scene_for(strip, width=None, height=None):
    scene = bpy.context.scene
    world = bpy.data.worlds.new('w')
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Strength'].default_value = 0.0
    scene.world = world
    # земля — только для AO (камере не видна)
    bpy.ops.mesh.primitive_plane_add(size=8, location=((width or 0) * 0.5 if strip else 0, 0, 0))
    ground = bpy.context.active_object
    ground.visible_camera = False
    if strip:
        eg.ortho_camera(scene, (width * 0.5, -1.0, height * 0.5), (math.radians(90), 0, 0), width)
    else:
        eg.ortho_camera(scene, (0, 0, 1.0), (0, 0, 0), 1024 * TEXEL)
    return scene, ground


def bake_region(name, tmp):
    """Рендер области: полоса — без строк повтора (они добавляются при сборке), плитка — с подбором покрытия."""
    reg = REGIONS[name]
    eg.reset_scene()
    width = reg['w'] * TEXEL
    render_h = reg['h'] - reg['pad']
    stats = {}
    strip = reg['kind'] == 'strip'
    if strip:
        if name in STRIPS:
            geo, stats = build_strip(name, STRIPS[name], width)
        else:
            geo, stats = build_sprout_strip(name, width)
        ob = geo.to_object(name)
        setup_scene_for(True, width, render_h * TEXEL)
    else:
        # покрытие: пробный рендер альбедо, число кусков — под цель (покрытие 1 − e^(−k·n))
        target = MULCH[name]['cover'] if name in MULCH else SPROUT_TOP_COVER
        count = MULCH[name]['pieces'] if name in MULCH else 700
        for attempt in range(4):
            eg.reset_scene()
            geo, stats = build_mulch(name, MULCH[name], count) if name in MULCH else build_sprout_tile(name, count)
            ob = geo.to_object(name)
            scene, _ = setup_scene_for(False)
            eg.setup_card_render(scene, reg['w'], reg['h'], samples=4)
            scene.render.image_settings.file_format = 'OPEN_EXR'
            path = os.path.join(tmp, 'probe.exr')
            eg.render_with_override(scene, [ob], pass_material('albedo'), path)
            a = read_exr(path)[..., 3]
            cover = float((a[tile_inner_mask(a.shape)] > 0.4).mean())
            stats['cover_probe'] = round(cover, 3)
            print('TILE_COVER', name, count, round(cover, 3), flush=True)
            if abs(cover - target) < 0.015 or attempt == 3:
                break
            count = int(count * math.log(1 - target) / math.log(max(1e-3, 1 - cover)))
        stats['count'] = count
    ob = bpy.data.objects[name]
    passes = render_passes(bpy.context.scene, [ob], reg['w'], render_h, tmp, strip)
    return passes, stats


SPROUT_TOP_COVER = 0.22


def tile_inner_mask(shape):
    """Внутренняя часть пятна (радиус < 0.65 полуширины плитки — до начала мягкого края у самых узких лопастей)."""
    h, w = shape[:2]
    yy, xx = np.mgrid[0:h, 0:w]
    rr = np.hypot(xx - w / 2 + 0.5, yy - h / 2 + 0.5) / (w / 2)
    return rr < 0.65


def tile_polygon_mask(shape):
    """Пиксели плитки внутри 12-угольника подстилки (радиус углов STRAW_R / STRAW_SCALE)."""
    h, w = shape[:2]
    yy, xx = np.mgrid[0:h, 0:w]
    px = (xx - w / 2 + 0.5) * TEXEL
    py = (yy - h / 2 + 0.5) * TEXEL
    ang = np.mod(np.arctan2(py, px), 2 * math.pi / STRAW_SIDES) - math.pi / STRAW_SIDES
    apothem = STRAW_R * math.cos(math.pi / STRAW_SIDES) / STRAW_SCALE
    return np.hypot(px, py) * np.cos(ang) < apothem


def compose_atlas(out_dir, regions=None):
    tmp = os.path.join(out_dir, '_bake')
    os.makedirs(tmp, exist_ok=True)
    layout = {}
    cache = os.path.join(tmp, 'regions.npz')
    stored = dict(np.load(cache)) if os.path.exists(cache) and regions else {}
    stats_path = os.path.join(tmp, 'stats.json')
    all_stats = json.load(open(stats_path)) if os.path.exists(stats_path) and regions else {}
    for tex, spec in TEXTURES.items():
        W, H = spec['w'], spec['h']
        albedo = np.zeros((H, W, 4), np.float32)
        normal = np.zeros((H, W, 3), np.float32) + np.array([0.5, 0.5, 1.0], np.float32)
        mask = np.zeros((H, W, 4), np.float32)
        for name in spec['regions']:
            reg = REGIONS[name]
            if regions and name not in regions and name + '_albedo' in stored:
                alb, nrm, msk = stored[name + '_albedo'], stored[name + '_normal'], stored[name + '_mask']
            else:
                passes, stats = bake_region(name, tmp)
                all_stats[name] = stats
                alb_px = unpremultiply(passes['albedo'])
                a = np.clip(alb_px[..., 3], 0, 1)
                rgb = push_pull(alb_px[..., :3], (a > 0.02).astype(np.float32) * a)
                alb = np.concatenate([rgb, a[..., None]], -1)
                n = unpremultiply(passes['normal'])[..., :3] * 2 - 1
                n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6)
                n = push_pull(n, (a > 0.3).astype(np.float32))
                n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6)
                nrm = n * 0.5 + 0.5
                ao = unpremultiply(passes['ao'])[..., 0]
                sm = unpremultiply(passes['smooth'])[..., 0]
                filled = push_pull(np.stack([ao, sm], -1), (a > 0.3).astype(np.float32))
                msk = np.zeros(a.shape + (4,), np.float32)
                msk[..., 1] = np.clip(filled[..., 0], 0, 1)
                msk[..., 3] = np.clip(filled[..., 1], 0, 1)
                if reg['pad']:
                    # строки повтора низа полосы: юбка карточки и дальние мипы берут их
                    rep = lambda arr: np.concatenate([arr, np.repeat(arr[-1:], reg['pad'], 0)], 0)  # noqa: E731
                    alb, nrm, msk = rep(alb), rep(nrm), rep(msk)
                stored[name + '_albedo'], stored[name + '_normal'], stored[name + '_mask'] = alb, nrm, msk
            x, y, w, h = reg['x'], reg['y'], reg['w'], reg['h']
            albedo[y:y + h, x:x + w] = alb
            normal[y:y + h, x:x + w] = nrm
            mask[y:y + h, x:x + w] = msk
            a = alb[..., 3]
            info = dict(reg)
            st = all_stats.get(name, {})
            if reg['kind'] == 'strip':
                base_px = h - reg['pad']
                rows = np.where((a[:base_px] > 0.35).mean(1) > 0.002)[0]
                top_row = int(rows.min()) if len(rows) else 0
                info['base_px'] = base_px
                info['content_h'] = round((base_px - top_row) * TEXEL + 0.003, 4)
                prof = [(a[base_px - 1 - int(z / TEXEL)] > 0.35).mean() for z in (0.005, 0.05, 0.1, 0.15)
                        if int(z / TEXEL) < base_px]
                info['coverage_by_height'] = [round(float(p), 3) for p in prof]
                info['edges_px'] = [int(round(e / TEXEL)) for e in st.get('edges', [0.0, w * TEXEL])]
                info['edges_px'][-1] = w
            else:
                info['coverage_inner'] = round(float((a[tile_inner_mask(a.shape)] > 0.35).mean()), 3)
                info['coverage_polygon'] = round(float((a[tile_polygon_mask(a.shape)] > 0.35).mean()), 3)
            visible = a > 0.35
            srgb = np.where(alb[..., :3] <= 0.0031308, alb[..., :3] * 12.92,
                            1.055 * np.clip(alb[..., :3], 0, None) ** (1 / 2.4) - 0.055)
            info['mean_srgb'] = [int(round(v)) for v in srgb[visible].mean(0) * 255] if visible.any() else None
            info['stats'] = {k: v for k, v in st.items() if k != 'edges'}
            layout[name] = info
        prefix = f'{ATLAS_NAME}_{tex}'
        eg.save_png(albedo, os.path.join(out_dir, prefix + '_albedo.png'))
        eg.save_png(normal, os.path.join(out_dir, prefix + '_normal.png'), linear=True)
        eg.save_png(mask, os.path.join(out_dir, prefix + '_mask.png'), linear=True)
    np.savez(cache, **stored)
    with open(stats_path, 'w') as f:
        json.dump(all_stats, f)
    with open(os.path.join(out_dir, 'atlas_layout.json'), 'w', encoding='utf-8') as f:
        json.dump(dict(texel=TEXEL, textures={k: dict(w=v['w'], h=v['h']) for k, v in TEXTURES.items()}, regions=layout), f,
                  indent=1)
    print('ATLAS_OK', json.dumps({k: (v.get('content_h'), v.get('coverage_by_height'), v.get('coverage_inner'),
                                      v.get('coverage_polygon'), v['mean_srgb']) for k, v in layout.items()}))
    return layout


# ---------------------------------------------------------------------------------------------- карточки ассетов

def region_uv(reg, px, py):
    """Пиксель текстуры (x вправо, y вниз от верха картинки) -> UV (V вверх)."""
    return (px / reg['tex_w'], 1.0 - py / reg['tex_h'])


def wind_alpha(z):
    return min(1.0, (max(z, 0.0) / 0.2) ** 1.5) * 0.6


def pick_span(edges, target_px, rng):
    """Целые участки полосы (индексы границ) общей длиной около target_px: карточка кончается на сужении участка."""
    starts = list(range(len(edges) - 1))
    rng.shuffle(starts)
    best, best_err = None, 1e9
    for i in starts:
        for j in range(i + 1, len(edges)):
            L = edges[j] - edges[i]
            err = abs(L - target_px) / target_px
            if err < best_err:
                best, best_err = (edges[i], edges[j]), err
            if L > target_px:
                break
        if best_err < 0.12:
            break
    return best


def vertical_card(mb, normals, rng, reg, span, centre, yaw, cfg, mat):
    """Вертикальная карточка: segs звеньев с изломом в плане (±kink), один ряд по высоте и подземная юбка (skirt, она
    берёт строки повтора низа полосы), наклон верха lean. U — целые участки полосы (иногда зеркально), V — от земли вверх
    1:1 (растяжение по высоте height_scale)."""
    segs = cfg['segments']
    u0, u1 = span
    L = (u1 - u0) * TEXEL
    hs = rng.uniform(*cfg['height_scale'])
    H = reg['content_h'] * hs
    d = np.array([math.cos(yaw), math.sin(yaw), 0.0])
    pts = [np.zeros(3)]
    dirs = []
    for k in range(segs):
        if k:
            d = rot(UP, math.radians(rng.uniform(*cfg['kink'])) * rng.choice([-1, 1])) @ d
        dirs.append(d.copy())
        pts.append(pts[-1] + d * L / segs)
    pts = np.array(pts)
    pts -= (pts[0] + pts[-1]) * 0.5
    pts += np.array([centre[0], centre[1], 0.0])
    lean_angle = math.radians(rng.uniform(*cfg['lean']))
    lean_az = rng.uniform(0, math.tau)
    lean_v = np.array([math.cos(lean_az), math.sin(lean_az), 0.0]) * math.tan(lean_angle) * H
    zs = ([-cfg['skirt']] if cfg['skirt'] > 0 else []) + [0.0, H]
    mirror = rng.random() < 0.5
    base_v = reg['y'] + reg['base_px']
    nf, nu = cfg['normal_face'], cfg['normal_up']
    col_normals = []
    for k in range(segs + 1):
        f = np.zeros(3)
        if k > 0:
            f += np.cross(dirs[k - 1], UP)
        if k < segs:
            f += np.cross(dirs[k], UP)
        col_normals.append(unit(unit(f) * nf + UP * nu))
    grid = []
    for z in zs:
        row = []
        for k in range(segs + 1):
            t = max(z, 0.0) / H
            p = pts[k] + lean_v * t ** 1.4 + UP * z
            frac = k / segs if not mirror else 1 - k / segs
            u_px = reg['x'] + u0 + (u1 - u0) * frac
            v_px = base_v + 0.7 * reg['pad'] if z < 0 else base_v - (z / hs) / TEXEL
            alpha = wind_alpha(z) * cfg['wind'] if z > 0 else 0.0
            row.append((mb.vert(tuple(p)), region_uv(reg, u_px, v_px), (1.0, 1.0, 1.0, alpha), col_normals[k]))
        grid.append(row)
    for r in range(len(zs) - 1):
        for k in range(segs):
            a, b, c, e = grid[r][k], grid[r][k + 1], grid[r + 1][k + 1], grid[r + 1][k]
            mb.face([a[0], b[0], c[0], e[0]], mat, [a[1], b[1], c[1], e[1]], None, [a[2], b[2], c[2], e[2]])
            normals.extend([a[3], b[3], c[3], e[3]])
    return H, L


def straw_card(mb, normals, rng, reg, dome, mat):
    """Подстилка: 12-угольник (радиус углов STRAW_R) веером из центра, куполом — центр dome[0], край dome[1] над землёй;
    плитка растянута в STRAW_SCALE раз, повёрнута случайно."""
    cx, cy = reg['x'] + reg['w'] / 2, reg['y'] + reg['h'] / 2
    yaw = rng.uniform(0, math.tau)
    c0 = mb.vert((0.0, 0.0, dome[0]))
    uv0 = region_uv(reg, cx, cy)
    ids, uvs = [], []
    for k in range(STRAW_SIDES):
        a = k * math.tau / STRAW_SIDES
        lx, ly = STRAW_R * math.cos(a), STRAW_R * math.sin(a)
        wx, wy = lx * math.cos(yaw) - ly * math.sin(yaw), lx * math.sin(yaw) + ly * math.cos(yaw)
        ids.append(mb.vert((wx, wy, dome[1])))
        uvs.append(region_uv(reg, cx + lx / (STRAW_SCALE * TEXEL), cy - ly / (STRAW_SCALE * TEXEL)))
    for k in range(STRAW_SIDES):
        k1 = (k + 1) % STRAW_SIDES
        mb.face([c0, ids[k], ids[k1]], mat, [uv0, uvs[k], uvs[k1]], None, [(1.0, 1.0, 1.0, 0.0)] * 3)
        normals.extend([UP, UP, UP])


def family_cards(rng, cfg):
    """Стерня: карточки в трёх семействах через ~60° (±10°), в семействе параллельные через gap, длина — хорда круга
    spread на своём смещении (центры не дальше spread)."""
    R = cfg['spread']
    th0 = rng.uniform(0, math.pi)
    fams = [th0, th0 + math.radians(60 + rng.uniform(-10, 10)), th0 + math.radians(120 + rng.uniform(-10, 10))]
    counts = list(cfg['family_counts'])
    rng.shuffle(counts)
    out = []
    for th, n in zip(fams, counts):
        d = np.array([math.cos(th), math.sin(th)])
        perp = np.array([-math.sin(th), math.cos(th)])
        gap = rng.uniform(*cfg['gap'])
        shift = rng.uniform(-0.1, 0.1)
        for i in range(n):
            o = (i - (n - 1) / 2) * gap + shift
            chord = 2 * math.sqrt(max(R * R - o * o, 0.04))
            target = min(cfg['length'][1], max(cfg['length'][0], chord * rng.uniform(0.85, 1.0)))
            out.append(dict(th=th, d=d, perp=perp, o=o, chord=chord, target=target))
    return out


def scattered_cards(rng, cfg):
    """Всходы: центры в круге spread (с отступом min_gap), направления — три семейства через ~60° с разбросом ±8°."""
    th0 = rng.uniform(0, math.pi)
    fams = [th0, th0 + math.radians(60), th0 + math.radians(120)]
    centres, out = [], []
    for k in range(cfg['cards']):
        for _ in range(300):
            a, r = rng.uniform(0, math.tau), cfg['spread'] * math.sqrt(rng.random())
            p = np.array([r * math.cos(a), r * math.sin(a)])
            if all(np.linalg.norm(p - q) >= cfg['min_gap'] for q in centres):
                break
        centres.append(p)
        th = fams[k % 3] + math.radians(rng.uniform(-8, 8))
        out.append(dict(th=th, centre=p, target=rng.uniform(*cfg['length'])))
    return out


STUBBLE_CARDS = dict(layout='families', family_counts=(3, 3, 2), gap=(0.20, 0.25), length=(0.6, 1.4), spread=0.66,
                     segments=4, kink=(3, 9), lean=(1, 5), height_scale=(0.97, 1.03), skirt=0.06, wind=0.2,
                     normal_face=0.42, normal_up=0.91)
SPROUT_CARDS = dict(layout='scattered', cards=16, length=(0.4, 0.6), spread=0.72, min_gap=0.13, segments=2, kink=(4, 12),
                    lean=(2, 10), height_scale=(0.9, 1.1), skirt=0.0, wind=1.0, normal_face=0.42, normal_up=0.91)

ASSETS = {
    # materials — (карточки, подстилка) = подмеши 0 и 1; dome — купол подстилки (центр, край), у каждого ассета свой
    'field_stubble_fresh_01': dict(materials=('field_cards_stubble_fresh', 'field_cards_straw_fresh'),
                                   strips=[('fresh_a', 0.5), ('fresh_c', 0.5)], mulch='mulch_fresh', cards=STUBBLE_CARDS,
                                   dome=(0.010, 0.004)),
    'field_stubble_fresh_02': dict(materials=('field_cards_stubble_fresh', 'field_cards_straw_fresh'),
                                   strips=[('fresh_b', 0.5), ('fresh_d', 0.5)], mulch='mulch_fresh', cards=STUBBLE_CARDS,
                                   dome=(0.012, 0.005)),
    'field_stubble_aged_01': dict(materials=('field_cards_stubble_aged', 'field_cards_straw_aged'),
                                  strips=[('aged_a', 0.25), ('aged_b', 0.25), ('aged_c', 0.2), ('aged_low', 0.15),
                                          ('aged_low_b', 0.15)],
                                  mulch='mulch_aged', cards=dict(STUBBLE_CARDS, lean=(1, 8), height_scale=(0.95, 1.05)),
                                  dome=(0.009, 0.0035)),
    'field_sprouts_01': dict(materials=('field_cards_sprouts', 'field_cards_sprouts_top'),
                             strips=[('sprout_side_a', 0.25), ('sprout_side_b', 0.25), ('sprout_side_c', 0.25),
                                     ('sprout_side_d', 0.25)],
                             mulch='sprout_top', cards=SPROUT_CARDS, dome=(0.007, 0.003)),
}


def build_asset(name, cfg, layout):
    cc = cfg['cards']
    rng = random.Random(zlib.crc32((name + '_cards2').encode()))
    mb = eg.MeshBuilder()
    normals = []
    regs = layout['regions']
    names = [s for s, _ in cfg['strips']]
    weights = [w for _, w in cfg['strips']]
    heights, lengths = [], []
    plan = family_cards(rng, cc) if cc['layout'] == 'families' else scattered_cards(rng, cc)
    for card in plan:
        reg = regs[rng.choices(names, weights)[0]]
        span = pick_span(reg['edges_px'], card['target'] / TEXEL, rng)
        L = (span[1] - span[0]) * TEXEL
        if cc['layout'] == 'families':
            s = rng.uniform(-1, 1) * max(0.0, (card['chord'] - L) * 0.5)
            centre = card['perp'] * card['o'] + card['d'] * s
        else:
            centre = card['centre']
        yaw = card['th'] + (math.pi if rng.random() < 0.5 else 0.0)
        H, L = vertical_card(mb, normals, rng, reg, span, centre, yaw, cc, 0)
        heights.append(H)
        lengths.append(L)
    straw_card(mb, normals, rng, regs[cfg['mulch']], cfg['dome'], 1)
    mats = [eg.emission_material(m, lambda nt: nt.nodes.new('ShaderNodeRGB').outputs[0]) for m in cfg['materials']]
    obj = mb.to_object(name, mats)
    eg.set_custom_normals(obj, normals)
    return obj, mb.triangle_count, dict(cards=len(plan), card_length_total=round(sum(lengths), 2),
                                        card_lengths=[round(v, 2) for v in lengths], max_card_h=round(max(heights), 3))


BASE_MATERIAL = dict(role='grass', alphaDrag=1.0, initialBend=1.0, normalScale=0.5,
                     healthyColor=[1.0, 1.0, 1.0], rotorWashClear=0.0, invertBackfaceNormal=0.0, aoMax=1.0,
                     smoothnessMin=0.0, distanceControl=1.0, scaleFade=[100000, 40], sizeVariation=0.08,
                     colorNoiseSpread=0.3, specular=0.2,
                     # оттенок ветра (_WINDCOLORMIX_ON образца травы) выключить: он темнит и желтит цвет на ~(0.81, 0.7,
                     # 0.7) и пульсирует с порывами; импортёр читает поле, когда в MaterialEntry появится windColorMix
                     windColorMix=0.0)
STUBBLE_PROPS = dict(alphaCutoff=0.35, stiffness=0.9, drag=0.3, shiverDrag=0.02, shiverDirectionality=0.5, aoMin=0.5,
                     puddleSink=0.04, seasonKeep=2.0)
SPROUT_PROPS = dict(alphaCutoff=0.4, stiffness=1.0, drag=0.4, shiverDrag=0.04, shiverDirectionality=0.6, aoMin=0.6,
                    puddleSink=0.06, seasonKeep=1.0, smoothnessMax=0.3, dryColor=[0.94, 0.96, 0.92])
MATERIALS = {
    # мёртвая солома — по сезону не перекрашивается (seasonKeep 2); dryColor почти серый: разброс шумом темнит, не желтит;
    # puddleSink 0.04: стерня стоит из лужи, солома (6–12 мм) уходит под воду; texture — набор текстур
    'field_cards_stubble_fresh': dict(STUBBLE_PROPS, texture='fresh_strips', smoothnessMax=0.2, dryColor=[0.92, 0.92, 0.91]),
    'field_cards_straw_fresh': dict(STUBBLE_PROPS, texture='fresh_mulch', smoothnessMax=0.2, dryColor=[0.92, 0.92, 0.91]),
    'field_cards_stubble_aged': dict(STUBBLE_PROPS, texture='aged_strips', smoothnessMax=0.12, dryColor=[0.91, 0.91, 0.9]),
    'field_cards_straw_aged': dict(STUBBLE_PROPS, texture='aged_mulch', smoothnessMax=0.12, dryColor=[0.91, 0.91, 0.9]),
    # всходы озимых — зелёные круглый год (seasonKeep 1)
    'field_cards_sprouts': dict(SPROUT_PROPS, texture='sprouts_side'),
    'field_cards_sprouts_top': dict(SPROUT_PROPS, texture='sprouts_top'),
}
# прореживание вдали выключено (1.0): Nature Renderer убирает пятно 1.5 м целиком — дырой, а по чему он меряет долю
# кадра (высота 0.2 м или габарит 1.5 м) не проверено
NATURE_RENDERER = dict(renderDistance=100.0, shadows=False, densityInDistance=1.0)


def build_assets(out_dir):
    with open(os.path.join(out_dir, 'atlas_layout.json'), encoding='utf-8') as f:
        layout = json.load(f)
    report = {}
    for name, cfg in ASSETS.items():
        folder = os.path.join(out_dir, name)
        os.makedirs(folder, exist_ok=True)
        for old in os.listdir(folder):
            if old.startswith(ATLAS_NAME) and old.endswith('.png'):
                os.remove(os.path.join(folder, old))
        materials = []
        for material_id in cfg['materials']:
            props = dict(MATERIALS[material_id])
            tex = props.pop('texture')
            prefix = f'{ATLAS_NAME}_{tex}'
            for suffix in ('albedo', 'normal', 'mask'):
                shutil.copyfile(os.path.join(out_dir, f'{prefix}_{suffix}.png'), os.path.join(folder, f'{prefix}_{suffix}.png'))
            tile = TEXTURES[tex]['regions']
            is_tile = any(r[4] == 'tile' for r in tile.values())
            materials.append(dict(BASE_MATERIAL, id=material_id, albedo=prefix + '_albedo.png', normal=prefix + '_normal.png',
                                  mask=prefix + '_mask.png', **props,
                                  texel=round(TEXEL * (STRAW_SCALE if is_tile else 1.0), 6)))  # texel — для предпросмотра
        eg.reset_scene()
        obj, tris, stats = build_asset(name, cfg, layout)
        bb = np.array([obj.matrix_world @ Vector(c) for c in obj.bound_box])
        eg.export_fbx([obj], os.path.join(folder, name + '.fbx'))
        manifest = eg.write_manifest(folder, name, 'detail', 'Field',
                                     [dict(fbx=name + '.fbx', screen=0.0, submeshes=list(cfg['materials']), castShadows=False)],
                                     materials)
        manifest['nr'] = dict(NATURE_RENDERER)
        with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
            json.dump(manifest, f, indent=1)
        report[name] = dict(triangles=tris, size=[round(float(bb[:, 0].max() - bb[:, 0].min()), 3),
                                                  round(float(bb[:, 1].max() - bb[:, 1].min()), 3)],
                            z=[round(float(bb[:, 2].min()), 3), round(float(bb[:, 2].max()), 3)],
                            straw_area=round(0.5 * STRAW_SIDES * STRAW_R ** 2 * math.sin(math.tau / STRAW_SIDES), 3), **stats)
    with open(os.path.join(out_dir, 'field_cards_report.json'), 'w', encoding='utf-8') as f:
        json.dump(report, f, indent=1)
    print('ASSETS_OK', json.dumps(report))


# --------------------------------------------------------------------------------------------------- предпросмотр

def unity_mips(rgba, cutoff=None, levels=7):
    """Мипы как у импортёра Unity: среднее 2×2 (цвет — линейно), альфа с сохранением покрытия при пороге cutoff
    (mipMapsPreserveCoverage: доля текселей выше порога — как в нулевом мипе, по всей текстуре)."""
    out = [rgba]
    cur = rgba
    base_cov = float((rgba[..., 3] > cutoff).mean()) if cutoff is not None else None
    for _ in range(levels - 1):
        h, w = cur.shape[0] // 2, cur.shape[1] // 2
        cur = cur.reshape(h, 2, w, 2, -1).mean((1, 3))
        nxt = cur.copy()
        if cutoff is not None:
            a = cur[..., 3]
            lo, hi = 0.1, 16.0
            for _ in range(30):
                mid = (lo + hi) / 2
                if float((np.clip(a * mid, 0, 1) > cutoff).mean()) < base_cov:
                    lo = mid
                else:
                    hi = mid
            nxt[..., 3] = np.clip(a * (lo + hi) / 2, 0, 1)
        out.append(nxt)
    return out


def load_png_linear(path, srgb):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4).copy()  # строки снизу вверх, как в Blender
    bpy.data.images.remove(img)
    if srgb:
        rgb = px[..., :3]
        px[..., :3] = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    return px


def make_image(name, px):
    h, w = px.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=True, float_buffer=True)
    img.colorspace_settings.name = 'Non-Color'
    img.alpha_mode = 'CHANNEL_PACKED'
    img.pixels.foreach_set(np.ascontiguousarray(px, np.float32).ravel())
    img.pack()
    return img


class Nodes:
    def __init__(self, mat):
        self.nt = mat.node_tree
        self.nt.nodes.clear()

    def new(self, kind, **props):
        n = self.nt.nodes.new(kind)
        for k, v in props.items():
            setattr(n, k, v)
        return n

    def link(self, a, b):
        self.nt.links.new(a, b)

    def put(self, socket, value):
        if isinstance(value, (int, float)):
            socket.default_value = value
        elif isinstance(value, tuple):
            socket.default_value = value
        else:
            self.link(value, socket)

    def math(self, op, a, b=None, clamp=False):
        n = self.new('ShaderNodeMath', operation=op, use_clamp=clamp)
        self.put(n.inputs[0], a)
        if b is not None:
            self.put(n.inputs[1], b)
        return n.outputs[0]

    def vmath(self, op, a, b=None, scale=None, out=0):
        n = self.new('ShaderNodeVectorMath', operation=op)
        self.put(n.inputs[0], a)
        if b is not None:
            self.put(n.inputs[1], b)
        if scale is not None:
            self.put(n.inputs[3], scale)
        return n.outputs[out]

    def mix(self, kind, f, a, b):
        n = self.new('ShaderNodeMix', data_type=kind)
        suffix = {'FLOAT': 'Float', 'VECTOR': 'Vector', 'RGBA': 'Color'}[kind]
        ins = {s.identifier: s for s in n.inputs}
        self.put(ins['Factor_Float'], f)
        self.put(ins['A_' + suffix], a)
        self.put(ins['B_' + suffix], b)
        return {s.identifier: s for s in n.outputs}['Result_' + suffix]


def preview_material(name, chains, mat_entry, texel, k_pix, forced_mip, tint=None, ao_mix=0.4):
    """Материал как NM_Foliage (Nature Renderer) в Unity: альбедо × lerp(dry, healthy, шум) (× оттенок ветра tint, если
    задан), альфа-тест по порогу, нормаль: карта (x·s, y·s, lerp(1, z, s)) в базисе Mikk (атрибуты вершин) — без
    разворота на изнанке, свет одинаковый с обеих сторон (диффузный + просвечивание с обратной нормалью); мип — по
    расстоянию и углу (анизотропия 4)."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    N = Nodes(mat)
    out = N.new('ShaderNodeOutputMaterial')
    uv = N.new('ShaderNodeUVMap')
    alb_imgs, nrm_imgs = chains
    levels = len(alb_imgs)
    tex_a, tex_n = [], []
    for i in range(levels):
        ta = N.new('ShaderNodeTexImage', image=alb_imgs[i], interpolation='Linear', extension='EXTEND')
        tn = N.new('ShaderNodeTexImage', image=nrm_imgs[i], interpolation='Linear', extension='EXTEND')
        N.link(uv.outputs['UV'], ta.inputs['Vector'])
        N.link(uv.outputs['UV'], tn.inputs['Vector'])
        tex_a.append(ta)
        tex_n.append(tn)
    if forced_mip is not None:
        lod = float(forced_mip)
    elif texel is None:
        lod = 0.0
    else:
        cam = N.new('ShaderNodeCameraData')
        geo = N.new('ShaderNodeNewGeometry')
        footprint = N.math('MULTIPLY', cam.outputs['View Distance'], k_pix / texel)
        lod0 = N.math('LOGARITHM', N.math('MAXIMUM', footprint, 1e-4), 2.0)
        cosv = N.math('ABSOLUTE', N.vmath('DOT_PRODUCT', geo.outputs['Incoming'], geo.outputs['True Normal'], out=1))
        aniso = N.math('MAXIMUM', N.math('MULTIPLY', N.math('LOGARITHM', N.math('MAXIMUM', N.math('MULTIPLY', cosv, 4.0), 1e-3), 2.0), -1.0), 0.0)
        lod = N.math('ADD', lod0, aniso)
    colour = tex_a[levels - 1].outputs['Color']
    alpha = tex_a[levels - 1].outputs['Alpha']
    nrm = tex_n[levels - 1].outputs['Color']
    ao = tex_n[levels - 1].outputs['Alpha']
    for i in range(levels - 2, -1, -1):
        f = N.math('SUBTRACT', lod, float(i), clamp=True) if not isinstance(lod, float) else max(0.0, min(1.0, lod - i))
        colour = N.mix('RGBA', f, tex_a[i].outputs['Color'], colour)
        alpha = N.mix('FLOAT', f, tex_a[i].outputs['Alpha'], alpha)
        nrm = N.mix('VECTOR', f, tex_n[i].outputs['Color'], nrm)
        ao = N.mix('FLOAT', f, tex_n[i].outputs['Alpha'], ao)
    # цветовой шум NM_Foliage: lerp(dry, healthy, SimpleNoise(world xz, spread))
    geo2 = N.new('ShaderNodeNewGeometry')
    noise = N.new('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = max(0.05, mat_entry.get('colorNoiseSpread', 0.3))
    noise.inputs['Detail'].default_value = 2.0
    N.link(geo2.outputs['Position'], noise.inputs['Vector'])
    dry = tuple(mat_entry.get('dryColor', [1, 1, 1])) + (1.0,)
    healthy = tuple(mat_entry.get('healthyColor', [1, 1, 1])) + (1.0,)
    tint_n = N.mix('RGBA', noise.outputs['Fac'], dry, healthy)
    base = N.mix('RGBA', 1.0, colour, tint_n)
    base.node.blend_type = 'MULTIPLY'
    if tint is not None:
        base = N.mix('RGBA', 1.0, base, tuple(tint) + (1.0,))
        base.node.blend_type = 'MULTIPLY'
    # AO маски: в URP — только на рассеянный свет неба; приближение — доля ao_mix в цвете
    ao_lo, ao_hi = mat_entry.get('aoMin', 0.0), mat_entry.get('aoMax', 1.0)
    ao_r = N.math('ADD', ao_lo, N.math('MULTIPLY', ao, ao_hi - ao_lo))
    ao_f = N.math('ADD', 1.0 - ao_mix, N.math('MULTIPLY', ao_r, ao_mix))
    aoc = N.new('ShaderNodeCombineXYZ')
    for s in range(3):
        N.link(ao_f, aoc.inputs[s])
    base = N.mix('RGBA', 1.0, base, aoc.outputs[0])
    base.node.blend_type = 'MULTIPLY'
    # нормаль: (2c − 1), сила s
    s = mat_entry.get('normalScale', 1.0)
    sep = N.new('ShaderNodeSeparateXYZ')
    mad = N.new('ShaderNodeVectorMath', operation='MULTIPLY_ADD')
    N.link(nrm, mad.inputs[0])
    mad.inputs[1].default_value = (2.0, 2.0, 2.0)
    mad.inputs[2].default_value = (-1.0, -1.0, -1.0)
    N.link(mad.outputs[0], sep.inputs[0])
    nx = N.math('MULTIPLY', sep.outputs[0], s)
    ny = N.math('MULTIPLY', sep.outputs[1], s)
    nz = N.math('ADD', 1.0 - min(1.0, s), N.math('MULTIPLY', sep.outputs[2], min(1.0, s)))
    a_n = N.new('ShaderNodeAttribute', attribute_name='nrm', attribute_type='GEOMETRY')
    a_t = N.new('ShaderNodeAttribute', attribute_name='tan', attribute_type='GEOMETRY')
    a_s = N.new('ShaderNodeAttribute', attribute_name='tsgn', attribute_type='GEOMETRY')
    bit = N.vmath('SCALE', N.vmath('CROSS_PRODUCT', a_n.outputs['Vector'], a_t.outputs['Vector']), scale=a_s.outputs['Fac'])
    n_obj = N.vmath('ADD', N.vmath('ADD', N.vmath('SCALE', a_t.outputs['Vector'], scale=nx), N.vmath('SCALE', bit, scale=ny)),
                    N.vmath('SCALE', a_n.outputs['Vector'], scale=nz))
    vt = N.new('ShaderNodeVectorTransform', vector_type='NORMAL', convert_from='OBJECT', convert_to='WORLD')
    N.link(n_obj, vt.inputs['Vector'])
    n_world = N.vmath('NORMALIZE', vt.outputs['Vector'])
    bsdf = N.new('ShaderNodeBsdfPrincipled')
    N.link(base, bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value = 1.0 - 0.5 * mat_entry.get('smoothnessMax', 0.2)
    bsdf.inputs['Specular IOR Level'].default_value = 0.4
    N.link(n_world, bsdf.inputs['Normal'])
    back = N.new('ShaderNodeBsdfTranslucent')
    N.link(base, back.inputs['Color'])
    N.link(N.vmath('SCALE', n_world, scale=-1.0), back.inputs['Normal'])
    add = N.new('ShaderNodeAddShader')
    N.link(bsdf.outputs['BSDF'], add.inputs[0])
    N.link(back.outputs['BSDF'], add.inputs[1])
    # альфа-тест: (альфа × угасание экземпляра) > порог
    fade = N.new('ShaderNodeAttribute', attribute_name='fade', attribute_type='OBJECT')
    a_eff = N.math('MULTIPLY', alpha, fade.outputs['Fac'])
    keep = N.math('GREATER_THAN', a_eff, mat_entry.get('alphaCutoff', 0.5))
    transp = N.new('ShaderNodeBsdfTransparent')
    mix = N.new('ShaderNodeMixShader')
    N.link(keep, mix.inputs['Fac'])
    N.link(transp.outputs['BSDF'], mix.inputs[1])
    N.link(add.outputs['Shader'], mix.inputs[2])
    N.link(mix.outputs['Shader'], out.inputs['Surface'])
    return mat


def soil_material(hex_colour, flat=False):
    m = bpy.data.materials.new('soil')
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes['Principled BSDF']
    bsdf.inputs['Roughness'].default_value = 0.95
    bsdf.inputs['Specular IOR Level'].default_value = 0.3
    base = Vector(eg.hex_rgb(hex_colour))
    if flat:
        # ровный цвет (синяя земля для подсчёта видимой земли)
        bsdf.inputs['Base Color'].default_value = (*base, 1.0)
        return m
    tc = nt.nodes.new('ShaderNodeTexCoord')
    coarse = nt.nodes.new('ShaderNodeTexNoise')
    coarse.inputs['Scale'].default_value = 0.35
    coarse.inputs['Detail'].default_value = 6
    nt.links.new(tc.outputs['Object'], coarse.inputs['Vector'])
    ramp = nt.nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = 0.3
    ramp.color_ramp.elements[0].color = (*(base * 0.78), 1)
    ramp.color_ramp.elements[1].position = 0.72
    ramp.color_ramp.elements[1].color = (*(base * 1.18), 1)
    nt.links.new(coarse.outputs['Fac'], ramp.inputs['Fac'])
    mid = nt.nodes.new('ShaderNodeTexNoise')
    mid.inputs['Scale'].default_value = 6.0
    mid.inputs['Detail'].default_value = 4
    nt.links.new(tc.outputs['Object'], mid.inputs['Vector'])
    fine = nt.nodes.new('ShaderNodeTexNoise')
    fine.inputs['Scale'].default_value = 90
    fine.inputs['Detail'].default_value = 3
    nt.links.new(tc.outputs['Object'], fine.inputs['Vector'])
    m1 = nt.nodes.new('ShaderNodeMix')
    m1.data_type = 'RGBA'
    m1.blend_type = 'MULTIPLY'
    ins = {s.identifier: s for s in m1.inputs}
    ins['Factor_Float'].default_value = 0.35
    nt.links.new(ramp.outputs['Color'], ins['A_Color'])
    nt.links.new(mid.outputs['Color'], ins['B_Color'])
    m2 = nt.nodes.new('ShaderNodeMix')
    m2.data_type = 'RGBA'
    m2.blend_type = 'MULTIPLY'
    ins2 = {s.identifier: s for s in m2.inputs}
    ins2['Factor_Float'].default_value = 0.3
    nt.links.new({s.identifier: s for s in m1.outputs}['Result_Color'], ins2['A_Color'])
    nt.links.new(fine.outputs['Color'], ins2['B_Color'])
    gain = nt.nodes.new('ShaderNodeMix')
    gain.data_type = 'RGBA'
    gain.blend_type = 'MULTIPLY'
    ins3 = {s.identifier: s for s in gain.inputs}
    ins3['Factor_Float'].default_value = 1.0
    ins3['B_Color'].default_value = (1.45, 1.45, 1.45, 1)
    nt.links.new({s.identifier: s for s in m2.outputs}['Result_Color'], ins3['A_Color'])
    nt.links.new({s.identifier: s for s in gain.outputs}['Result_Color'], bsdf.inputs['Base Color'])
    bump = nt.nodes.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = 0.3
    bump.inputs['Distance'].default_value = 0.01
    nt.links.new(fine.outputs['Fac'], bump.inputs['Height'])
    nt.links.new(bump.outputs['Normal'], bsdf.inputs['Normal'])
    return m


LIGHTS = {
    # кадр из игры без оттенка ветра материала (подгонка по старой стерне, делённая на ~(0.81, 0.70, 0.70))
    'game': dict(sun_col='1.0,0.91,0.79', sun_e=1.72, sky_col='0.86,0.93,1.0', sky=0.27),
    # прежняя подгонка (оттенок ветра сидел в свете)
    'old': dict(sun_col='1.0,0.785,0.686', sun_e=1.39, sky_col='0.55,0.51,0.55', sky=0.35),
    # нейтральный ясный день
    'neutral': dict(sun_col='1.0,0.97,0.92', sun_e=1.6, sky_col='0.6,0.7,0.85', sky=0.5),
}


def preview(args):
    p = argparse.ArgumentParser()
    p.add_argument('out')
    p.add_argument('--mix', required=True, help='папка_ассета:экз/м², через запятую')
    p.add_argument('--cam-h', type=float, default=0.8)
    p.add_argument('--pitch', type=float, default=30.0)
    p.add_argument('--vfov', type=float, default=70.0)
    p.add_argument('--res', default='1280x720')
    p.add_argument('--seed', type=int, default=3)
    p.add_argument('--samples', type=int, default=48)
    p.add_argument('--ground', default='#8A7B66')
    p.add_argument('--flat-ground', action='store_true', help='земля ровного цвета (для подсчёта видимой земли)')
    p.add_argument('--sun', type=float, default=40.0)
    p.add_argument('--sun-az', type=float, default=35.0)
    p.add_argument('--light', default='game', choices=sorted(LIGHTS))
    p.add_argument('--sun-e', type=float, default=None)
    p.add_argument('--sun-col', default=None)
    p.add_argument('--sky', type=float, default=None)
    p.add_argument('--sky-col', default=None)
    p.add_argument('--wind-tint', default=None, help='r,g,b — оттенок ветра _WINDCOLORMIX_ON (умножение цвета)')
    p.add_argument('--grey-card', default=None, help='x,y,размер — серая (50 %%) карточка на земле')
    p.add_argument('--mip', type=float, default=None, help='принудительный мип (2 = текстуры 1/4, 4 = 1/16)')
    p.add_argument('--cell', type=float, default=2.0)
    p.add_argument('--filter', type=float, default=0.75)
    p.add_argument('--nr-thin', default='auto',
                   help="прореживание Nature Renderer: auto — из манифеста (по высоте растения), off, или dens,f0,f1,size")
    p.add_argument('--single', action='store_true', help='один экземпляр в центре кадра')
    p.add_argument('--yaw', type=float, default=0.0)
    p.add_argument('--target', default=None, help='x,y,z точки взгляда (вместо pitch)')
    p.add_argument('--cam-xy', default='0,-1.5')
    p.add_argument('--dump', default=None, help='записать расстановку (x, y, поворот, масштаб, имя) в JSON')
    a = p.parse_args(args)
    light = dict(LIGHTS[a.light])
    for key in ('sun_col', 'sun_e', 'sky_col', 'sky'):
        if getattr(a, key) is not None:
            light[key] = getattr(a, key)
    tint = [float(v) for v in a.wind_tint.split(',')] if a.wind_tint else None
    res_x, res_y = (int(v) for v in a.res.split('x'))
    sc = eg.reset_scene()
    bpy.ops.mesh.primitive_plane_add(size=800)
    bpy.context.active_object.data.materials.append(soil_material(a.ground, a.flat_ground))
    if a.grey_card:
        gx, gy, gs = (float(v) for v in a.grey_card.split(','))
        bpy.ops.mesh.primitive_plane_add(size=gs, location=(gx, gy, 0.02))
        gm = bpy.data.materials.new('grey50')
        gm.use_nodes = True
        gb = gm.node_tree.nodes['Principled BSDF']
        gb.inputs['Base Color'].default_value = (0.2158, 0.2158, 0.2158, 1.0)  # sRGB 128
        gb.inputs['Roughness'].default_value = 1.0
        gb.inputs['Specular IOR Level'].default_value = 0.0
        bpy.context.active_object.data.materials.append(gm)
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    sc.collection.objects.link(cam)
    sc.camera = cam
    cam.data.sensor_fit = 'VERTICAL'
    cam.data.angle_y = math.radians(a.vfov)
    cam.data.clip_start = 0.02
    cam.data.clip_end = 2000
    cx, cy = (float(v) for v in a.cam_xy.split(','))
    cam.location = (cx, cy, a.cam_h)
    if a.target:
        t = Vector([float(v) for v in a.target.split(',')])
        cam.rotation_euler = (t - cam.location).to_track_quat('-Z', 'Y').to_euler()
    else:
        cam.rotation_euler = (math.radians(90 - a.pitch), 0, math.radians(a.yaw))
    bpy.context.view_layer.update()
    cam_inv = cam.matrix_world.inverted()
    tan_y = math.tan(math.radians(a.vfov) / 2)
    tan_x = tan_y * res_x / res_y
    k_pix = 2 * tan_y / res_y  # угол пикселя, рад
    cam_pos = Vector(cam.location)
    chains = {}
    entries = []
    for part in a.mix.split(','):
        folder, density = part.rsplit(':', 1)
        with open(os.path.join(folder, 'manifest.json'), encoding='utf-8') as f:
            manifest = json.load(f)
        mats = []
        for mat_entry in manifest['materials']:
            cutoff = mat_entry.get('alphaCutoff', 0.5)
            key = (os.path.normcase(os.path.join(folder, mat_entry['albedo'])), cutoff)
            if key not in chains:
                alb = load_png_linear(os.path.join(folder, mat_entry['albedo']), True)
                nrm = load_png_linear(os.path.join(folder, mat_entry['normal']), False)
                mask_path = os.path.join(folder, mat_entry['mask']) if mat_entry.get('mask') else None
                if mask_path and os.path.exists(mask_path):
                    nrm[..., 3] = load_png_linear(mask_path, False)[..., 1]
                else:
                    nrm[..., 3] = 1.0
                levels = 7 if min(alb.shape[:2]) >= 512 else 1
                amips = unity_mips(alb, cutoff, levels)
                nmips = unity_mips(nrm, None, levels)
                tag = str(len(chains))
                chains[key] = ([make_image(f'a{tag}_{i}', m) for i, m in enumerate(amips)],
                               [make_image(f'n{tag}_{i}', m) for i, m in enumerate(nmips)])
            texel = mat_entry.get('texel', manifest.get('texel'))
            mats.append((mat_entry.get('id', ''), preview_material('m_' + os.path.basename(folder) + '_' + mat_entry.get('id', ''),
                                                                   chains[key], mat_entry, texel, k_pix, a.mip, tint)))
        bpy.ops.import_scene.fbx(filepath=os.path.join(folder, manifest['lods'][0]['fbx']))
        o = bpy.context.selected_objects[0]
        me = o.data
        # слоты меняются на месте: materials.clear() сбрасывает индексы материалов граней в 0
        slots = [m.name if m else '' for m in me.materials]
        if not slots:
            me.materials.append(mats[0][1])
        for i, sname in enumerate(slots):
            base_name = sname.rsplit('.', 1)[0] if sname[-4:-3] == '.' and sname[-3:].isdigit() else sname
            me.materials[i] = next((m for mid, m in mats if mid and base_name == mid), mats[min(i, len(mats) - 1)][1])
        print('PREVIEW_SLOTS', slots, [m.name for m in me.materials])
        uvname = me.uv_layers[0].name
        me.calc_tangents(uvmap=uvname)
        an = me.attributes.new('nrm', 'FLOAT_VECTOR', 'CORNER')
        at = me.attributes.new('tan', 'FLOAT_VECTOR', 'CORNER')
        asg = me.attributes.new('tsgn', 'FLOAT', 'CORNER')
        cn = me.corner_normals
        for i, loop in enumerate(me.loops):
            an.data[i].vector = cn[i].vector
            at.data[i].vector = loop.tangent
            asg.data[i].value = loop.bitangent_sign
        height = max(v.co.y for v in me.vertices) if o.rotation_euler.x else max(v.co.z for v in me.vertices)
        o.location = (0, 0, -100)
        o['fade'] = 1.0
        nr = manifest.get('nr') or {}
        mat0 = manifest['materials'][0]
        thin = None
        if a.nr_thin == 'auto':
            if 0 <= nr.get('densityInDistance', 1.0) < 1.0 and nr.get('densityFalloff'):
                thin = (nr['densityInDistance'], nr['densityFalloff'][0], nr['densityFalloff'][1], height)
        elif a.nr_thin != 'off':
            thin = tuple(float(v) for v in a.nr_thin.split(','))
        entries.append(dict(obj=o, density=float(density), sv=max(0.0, mat0.get('sizeVariation', 0.0)),
                            dist=nr.get('renderDistance', 0.0) or 150.0, fade=(mat0.get('scaleFade') or [0, 0])[1],
                            name=os.path.basename(folder), thin=thin))
        print('PREVIEW_ASSET', os.path.basename(folder), 'height', round(height, 3), 'thin', thin)
    noise_rng = random.Random(91)
    grid = np.array([[noise_rng.random() for _ in range(128)] for _ in range(128)])

    def value_noise(x, y):
        ix, iy = math.floor(x), math.floor(y)
        fx, fy = x - ix, y - iy
        fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
        g00, g10 = grid[ix % 128][iy % 128], grid[(ix + 1) % 128][iy % 128]
        g01, g11 = grid[ix % 128][(iy + 1) % 128], grid[(ix + 1) % 128][(iy + 1) % 128]
        return (g00 + (g10 - g00) * fx) * (1 - fy) + (g01 + (g11 - g01) * fx) * fy

    rng = random.Random(a.seed)
    placed = 0
    dump = []

    def put(item, x, y, yaw, scale, fade):
        nonlocal placed
        o = item['obj'].copy()
        sc.collection.objects.link(o)
        o.location = (x, y, 0)
        o.rotation_euler = (math.radians(90), 0, yaw)
        o.scale = (scale, scale, scale)
        o['fade'] = fade
        o.visible_shadow = False
        o.visible_diffuse = False
        o.visible_glossy = False
        o.visible_transmission = False
        placed += 1
        dump.append((round(x, 3), round(y, 3), round(yaw, 3), round(scale, 3), item['name']))

    def visible(x, y, r=1.0):
        pc = cam_inv @ Vector((x, y, 0.1))
        depth = -pc.z
        if depth < -r:
            return False
        dd = max(depth, 0.05)
        return abs(pc.x) < dd * tan_x + r * 1.5 and abs(pc.y) < dd * tan_y + r * 1.5

    if a.single:
        for k, item in enumerate(entries):
            put(item, k * 1.8, 0.0, math.radians(a.yaw), 1.0, 1.0)
    else:
        reach = max(e['dist'] for e in entries)
        nx0, nx1 = int(math.floor((cx - reach) / a.cell)), int(math.ceil((cx + reach) / a.cell))
        ny0, ny1 = int(math.floor((cy - reach) / a.cell)), int(math.ceil((cy + reach) / a.cell))
        for ix in range(nx0, nx1):
            for iy in range(ny0, ny1):
                for item in entries:
                    expected = item['density'] * a.cell * a.cell
                    n = int(expected) + (1 if rng.random() < expected - int(expected) else 0)
                    for _ in range(n):
                        x, y = (ix + rng.random()) * a.cell, (iy + rng.random()) * a.cell
                        yaw = rng.uniform(0, math.tau)
                        smaller = min(1.0, max(0.0, value_noise(x / 6 + 91.7, y / 6 + 91.7) * 0.6 + rng.random() * 0.4))
                        thin_roll = rng.random()
                        d = (Vector((x, y, 0)) - cam_pos).length
                        if d > item['dist'] or not visible(x, y):
                            continue
                        if item['thin']:
                            # Nature Renderer: доля кадра, которую занимает растение, от f0 до f1 — плотность до dens
                            nd, f0, f1, nsz = item['thin']
                            frac = nsz / (2 * max(d, 0.01) * tan_y)
                            tt = min(1.0, max(0.0, (f0 - frac) / (f0 - f1)))
                            if thin_roll > 1.0 - (1.0 - nd) * tt:
                                continue
                        s = 1.0 - item['sv'] * smaller * math.sqrt(smaller)
                        fade = 1.0
                        if item['fade'] > 0:
                            fade = min(1.0, max(0.0, (item['dist'] - d) / item['fade']))
                            s *= fade
                        if s <= 0.02:
                            continue
                        put(item, x, y, yaw, s, fade)
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = float(light['sun_e'])
    sun.angle = math.radians(1.0)
    sun.color = tuple(float(v) for v in str(light['sun_col']).split(','))
    so = bpy.data.objects.new('sun', sun)
    sc.collection.objects.link(so)
    so.rotation_euler = (math.radians(90 - a.sun), 0, math.radians(a.sun_az))
    world = bpy.data.worlds.new('w')
    sc.world = world
    world.use_nodes = True
    wnt = world.node_tree
    bg = wnt.nodes['Background']
    bg.inputs['Color'].default_value = tuple(float(v) for v in str(light['sky_col']).split(',')) + (1.0,)
    bg.inputs['Strength'].default_value = float(light['sky'])
    # в кадре — светлое небо как у Enviro днём (на освещение не влияет)
    seen = wnt.nodes.new('ShaderNodeBackground')
    seen.inputs['Color'].default_value = (0.78, 0.82, 0.88, 1.0)
    seen.inputs['Strength'].default_value = 1.0
    lp = wnt.nodes.new('ShaderNodeLightPath')
    mixw = wnt.nodes.new('ShaderNodeMixShader')
    wnt.links.new(lp.outputs['Is Camera Ray'], mixw.inputs['Fac'])
    wnt.links.new(bg.outputs['Background'], mixw.inputs[1])
    wnt.links.new(seen.outputs['Background'], mixw.inputs[2])
    wnt.links.new(mixw.outputs['Shader'], wnt.nodes['World Output'].inputs['Surface'])
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = a.samples
    sc.cycles.device = 'CPU'
    sc.cycles.use_denoising = True
    sc.cycles.max_bounces = 3
    sc.cycles.diffuse_bounces = 2
    sc.cycles.transparent_max_bounces = 128
    sc.render.filter_size = a.filter
    sc.render.resolution_x = res_x
    sc.render.resolution_y = res_y
    sc.render.resolution_percentage = 100
    sc.view_settings.view_transform = 'Standard'
    sc.view_settings.look = 'None'
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGB'
    sc.render.filepath = a.out
    if a.dump:
        with open(a.dump, 'w') as f:
            json.dump(dump, f)
    if os.environ.get('FC_DEBUG'):
        for m in bpy.data.materials:
            if m.node_tree:
                imgs = sorted({n.image.name + ' %dx%d' % tuple(n.image.size) for n in m.node_tree.nodes
                               if n.type == 'TEX_IMAGE' and n.image})
                print('DBG_MAT', m.name, imgs[:3])
        for e in entries:
            me = e['obj'].data
            uvl = me.uv_layers
            print('DBG_UV', [lay.name for lay in uvl], uvl.active.name, [lay.active_render for lay in uvl])
            for idx in range(len(me.materials)):
                us = [uvl.active.data[li].uv[:] for p in me.polygons if p.material_index == idx for li in p.loop_indices]
                if us:
                    print('DBG_UVRANGE', idx, round(min(u[0] for u in us), 3), round(max(u[0] for u in us), 3),
                          round(min(u[1] for u in us), 3), round(max(u[1] for u in us), 3))
        return
    bpy.ops.render.render(write_still=True)
    print('PREVIEW_OK', a.out, 'instances', placed)


def main():
    args = eg.script_args()
    cmd = args[0]
    if cmd == 'preview':
        preview(args[1:])
        return
    out_dir = args[1]
    os.makedirs(out_dir, exist_ok=True)
    if cmd in ('atlas', 'all'):
        compose_atlas(out_dir, args[2:] if cmd == 'atlas' and len(args) > 2 else None)
    if cmd in ('assets', 'all'):
        build_assets(out_dir)


main()
