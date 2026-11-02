# Наземные мелочи (Tools/TreeGen, дизайн, раздел 7): ветки и палки, сосновые веточки и шишки, галька, щебень, упавшие
# ветви и камни для лесной подстилки, полей, обочин и точки взлёта. Масштаб настоящий: веточки 4–30 мм, галька 2–8 см,
# камни 10–30 см. Все материалы — кора NatureManufacture (NM_Bark NR, роль bark, только слой Trunk, layermask_none),
# ветер статичный (_HeightDrag 5, _Drag 0.0001, _InitialBend 0, _Stiffness 1), цвет вершин (1, 1, 1, 0), металличность 0.
#  - ground_twigs_01..03 — детали Nature Renderer (пятно до 1.4 м, 35 м без теней): веточки дуба с палкой; веточки,
#    две палки и 30 % ободранных; сосновые веточки с шишками. Каждое звено лежит на земле (центр на 0.7 r), излом ±15°
#    на узлах (узлы чуть толще), вилки 25–60°, пеньки обломанных боковых веточек, обломанные концы с 2–3 зубцами
#    (длинный расщеп — блок волокон, почти ровный слом — блок торца с кольцами), тонкие концы — почкой; сечение слегка
#    овальное и неровное (не труба); веточка, что ложится поперёк другой, приподнята над ней узлом (без проникновений);
#  - ground_pebbles_01/02 — детали (диск 0.6 м, 25 м): колотые кремни (тёмные раковистые сколы, остатки бурой корки) и
#    комья мела; угловатый щебень гранит/известняк. Каждая галька — своя форма (16 ячеек атласа), лежит плашмя с
#    наклоном до 15°, утоплена на 30–45 %, низ в почве; часть под землёй срезана (остаётся юбка), треугольники — сверху;
#  - ground_branch_01..03 — упавшие ветви 2.2–3.0 м (объекты точки взлёта «Stumps and logs»): дуб, сосна (мутовки),
#    старая ветвь со мхом и ободранными вилками; вилки частью лежат, частью поднимаются дугой (до ~0.35 м), частью
#    обломаны; пивот — середина по X/Y на земле, ось утоплена на 1–2 см;
#  - ground_stone_01..04 — камни 10–30 см (объекты «Pebbles», «Small rocks»): мел, окатанный известняк с лишайником,
#    кремень, плитка песчаника 1 : 0.8 : 0.3. Пивот — низ камня (генератор точки взлёта сам утапливает на
#    SinkFraction; AO запечён с землёй на 25 % высоты, низ — в почве).
# Текстуры (свои, процедурные; без загрузок и сторонних ассетов):
#  - ground_wood 1024² (2048 px/м = 0.5 м на текстуру): столбцы коры, бесшовные по V (по U — внутри столбца, шов
#    цилиндра снизу, в земле; толстая ветвь, чей обхват шире столбца, берёт столбец целиком с крупнее текселем):
#    U 0–0.3125 серо-бурая кора опавших веток (15 % ободранных полос, лишайник, чечевички, налёт водорослей),
#    0.3125–0.46875 серебристая древесина без коры, 0.46875–0.625 кора сосновых веточек #7D5A3F (листовые подушечки),
#    0.625–0.75 старая кора со мхом #5C6B3A; справа U 0.75–1 блоки 256²: сверху вниз торец со свежими кольцами #B59C7A,
#    расщеп (волокна сходятся к острию), чешуи шишки (по U бесшовно), старый серый торец;
#  - ground_stone 2048×1024: слева 16 ячеек 256² галек, справа 4 ячейки 512² камней — запечены в Cycles с высокой
#    модели (икосфера 5/6 с fractal-смещением; EMIT — альбедо и гладкость, касательная NORMAL, AO 32 spp с землёй на
#    уровне заглубления), низкая — упрощённая высокая (Decimate), развёртка Smart UV.
# Маска NatureManufacture: R металличность 0, G AO, B высота, A гладкость. Нормали OpenGL (+Y вверх), линейно.
# Запуск (Blender 5.2, фоном):
#   blender.exe -b --factory-startup --python ground_gen.py -- [--out <папка>] [--step wood|stone|preview|all]
#       [--assets <имя> ...]
#   wood — текстура ground_wood и ветки (twigs, branch); stone — запечка ground_stone, галька и камни; preview — кадры.
import argparse
import json
import math
import os
import random
import shutil
import sys
import zlib

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector
from mathutils import noise as mnoise

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

OUT_DEFAULT = os.path.join(os.environ.get('TEMP', r'C:\Temp'), 'claude',
                           'C--Unity-Projects-interceptors-interceptor-drones',
                           'c286b6a9-8cf3-4575-b34b-3e039edbabd7', 'scratchpad', 'envgen', 'ground')
GROUP = 'Ground'
UP = Vector((0.0, 0.0, 1.0))
DOWN = Vector((0.0, 0.0, -1.0))

TWIG_ASSETS = ['ground_twigs_01', 'ground_twigs_02', 'ground_twigs_03']
BRANCH_ASSETS = ['ground_branch_01', 'ground_branch_02', 'ground_branch_03']
PEBBLE_ASSETS = ['ground_pebbles_01', 'ground_pebbles_02']
STONE_ASSETS = ['ground_stone_01', 'ground_stone_02', 'ground_stone_03', 'ground_stone_04']

# ------------------------------------------------------------------ раскладка ground_wood

WOOD_PX = 1024
WOOD_M = 0.5                    # 1024 px = 0.5 м (как у коры деревьев, 2048 px/м)
UV_PER_M = 1.0 / WOOD_M         # 2 единицы UV на метр
TEXEL_M = WOOD_M / WOOD_PX
COLUMNS = {'bark': (0, 320), 'bare': (320, 480), 'pine': (480, 640), 'moss': (640, 768)}  # x, px
BLOCKS = ['end', 'splinter', 'cone', 'end_old']   # блоки 256² в столбце x 768..1024, сверху вниз
INSET = 4.0 / WOOD_PX           # отступ от края столбца (мипы не тянут соседний)


def column_range(region):
    x0, x1 = COLUMNS[region]
    return x0 / WOOD_PX + INSET, x1 / WOOD_PX - INSET


def block_uv(name, s, t):
    """UV внутри блока: s, t в 0..1 (t вверх по картинке)."""
    k = BLOCKS.index(name)
    u0, v0 = 0.75, 1.0 - (k + 1) / 4.0
    pad = 3.0 / WOOD_PX
    return (u0 + pad + (0.25 - 2 * pad) * s, v0 + pad + (0.25 - 2 * pad) * t)


# ------------------------------------------------------------------ шум и карты (numpy, бесшовно)

def hexlin(h):
    return np.array(eg.hex_rgb(h), np.float32)


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def lerp3(a, b, t):
    return a + (b - a) * np.asarray(t, np.float32)[..., None]


def pnoise(h, w, py, px, seed, octaves=4, persistence=0.5, lac=2):
    """Бесшовный value noise (h, w) 0..1; базовые периоды py (по строкам) × px (по столбцам) клеток."""
    rng = np.random.default_rng(seed)
    out = np.zeros((h, w), np.float32)
    amp, total = 1.0, 0.0
    for _ in range(octaves):
        grid = rng.random((py, px)).astype(np.float32)
        y = np.arange(h, dtype=np.float32) / h * py
        x = np.arange(w, dtype=np.float32) / w * px
        yi = np.floor(y).astype(int)
        xi = np.floor(x).astype(int)
        fy = (y - yi)[:, None]
        fx = (x - xi)[None, :]
        fy = fy * fy * (3 - 2 * fy)
        fx = fx * fx * (3 - 2 * fx)
        a = grid[yi % py][:, xi % px]
        b = grid[yi % py][:, (xi + 1) % px]
        c = grid[(yi + 1) % py][:, xi % px]
        d = grid[(yi + 1) % py][:, (xi + 1) % px]
        out += amp * ((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy)
        total += amp
        amp *= persistence
        py *= lac
        px *= lac
    return out / total


def periodic_1d(n, seed, freqs=(1, 2, 3, 4, 6), amps=None):
    rng = np.random.default_rng(seed)
    t = np.arange(n, dtype=np.float32) / n
    amps = amps or [1.0 / f ** 0.9 for f in freqs]
    out = np.zeros(n, np.float32)
    for f, a in zip(freqs, amps):
        out += a * rng.uniform(0.5, 1.0) * np.sin(2 * np.pi * (f * t + rng.random()))
    return out / sum(amps)


def blur(a, radius):
    out = a.astype(np.float32).copy()
    for _ in range(3):
        acc = np.zeros_like(out)
        for d in range(-radius, radius + 1):
            acc += np.roll(out, d, 0)
        out = acc / (2 * radius + 1)
        acc = np.zeros_like(out)
        for d in range(-radius, radius + 1):
            acc += np.roll(out, d, 1)
        out = acc / (2 * radius + 1)
    return out


def normal_from_height(height_m, texel_m):
    """Нормаль OpenGL (+Y вверх по картинке; строки сверху вниз) из высоты в метрах, 0..1."""
    dx = (np.roll(height_m, -1, 1) - np.roll(height_m, 1, 1)) / (2 * texel_m)
    dy = (np.roll(height_m, 1, 0) - np.roll(height_m, -1, 0)) / (2 * texel_m)
    n = np.stack([-dx, -dy, np.ones_like(height_m)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def cavity(height, radii=(2, 6, 16), weights=(0.4, 0.4, 0.2), gain=3.0):
    ao = np.zeros_like(height)
    for r, w in zip(radii, weights):
        ao += w * np.maximum(blur(height, r) - height, 0)
    return np.clip(1.0 - ao * gain, 0.0, 1.0)


# ------------------------------------------------------------------ области ground_wood

def line_field(h, w, n_lines, seed, warp_amp=0.08, width=0.12):
    """Продольные линии (по V), бесшовные по U: n_lines вокруг, изгибы шумом. Возвращает (линия 0..1, номер линии)."""
    x = (np.arange(w, dtype=np.float32) + 0.5) / w
    warp = (pnoise(h, w, 4, 3, seed + 1, 3) - 0.5) * warp_amp + (pnoise(h, w, 30, 6, seed + 2, 2) - 0.5) * warp_amp * 0.25
    phase = (x[None, :] + warp) * n_lines
    lid = np.floor(phase).astype(int) % n_lines
    g = np.abs(phase - np.floor(phase) - 0.5) * 2
    return smoothstep(1 - width, 1.0, g), lid


def tex_bark(h, w, seed):
    """Кора опавших веточек дуба/граба/клёна: тёмная серо-бурая, тонкие продольные морщины, короткие продольные
    трещинки, чечевички поперёк, 15 % ободранных продольных полос серебристой древесины с рваным краем коры,
    лишайник мелкими пятнами (4 %), зеленоватый налёт водорослей, сырые тёмные и сухие серые участки."""
    rng = np.random.default_rng(seed)
    wr = pnoise(h, w, 6, 70, seed + 1, 3, 0.55)          # тонкие морщины вдоль
    fine = pnoise(h, w, 160, 90, seed + 2, 2, 0.5)
    big = pnoise(h, w, 5, 2, seed + 3, 3, 0.6)            # участки (у каждой веточки свой сдвиг U/V)
    lines, lid = line_field(h, w, 14, seed + 4, 0.12, 0.13)
    seg = smoothstep(0.55, 0.7, pnoise(h, w, 12, 14, seed + 5, 3))
    fiss = lines * seg * np.random.default_rng(seed + 50).uniform(0.4, 1.0, 14)[lid]
    rag = pnoise(h, w, 48, 60, seed + 6, 3)
    dn = pnoise(h, w, 2, 12, seed + 7, 4, 0.55) + (rag - 0.5) * 0.06
    t_dec = float(np.quantile(dn, 0.85))
    dec = smoothstep(t_dec - 0.003, t_dec + 0.003, dn)
    rim = smoothstep(t_dec - 0.03, t_dec - 0.003, dn) * (1 - dec)
    grain = pnoise(h, w, 3, 90, seed + 8, 2, 0.5)
    ln = pnoise(h, w, 48, 16, seed + 9, 4, 0.55) * (0.8 + 0.4 * fine)
    t_l = float(np.quantile(ln, 0.975))
    lichen = smoothstep(t_l, t_l + 0.02, ln) * (1 - dec)
    kind = pnoise(h, w, 8, 3, seed + 10, 2)
    lent = np.zeros((h, w), np.float32)
    n_l = h * w // 380
    lent[rng.integers(0, h, n_l), rng.integers(0, w, n_l)] = 1.0
    lent = np.maximum.reduce([lent, np.roll(lent, 1, 1), np.roll(lent, -1, 1)]) * (1 - dec)
    algae = smoothstep(0.5, 0.75, pnoise(h, w, 4, 3, seed + 11, 4)) * (1 - dec)
    damp = smoothstep(0.45, 0.8, pnoise(h, w, 3, 2, seed + 12, 3))
    along = pnoise(h, w, 6, 1, seed + 14, 3, 0.6)        # участки вдоль веточки (сырые/сухие), по всему обхвату
    t = np.clip(0.5 + (big - 0.5) * 1.3 + (along - 0.5) * 1.2 + (wr - 0.5) * 1.1 + (fine - 0.5) * 0.35, 0, 1)
    col = lerp3(hexlin('#4A3F33'), hexlin('#74685A'), t)
    col = lerp3(col, hexlin('#5E4B3A'), smoothstep(0.55, 0.8, pnoise(h, w, 3, 2, seed + 15, 3)) * 0.5)
    col = lerp3(col, col * np.array([0.78, 0.74, 0.70], np.float32), damp * 0.7)
    col = lerp3(col, hexlin('#5F624C'), algae * 0.45)
    bare = lerp3(hexlin('#756E62'), hexlin('#8E877B'), grain) * (0.9 + 0.2 * fine)[..., None]
    col = lerp3(col, bare, dec)
    col = col * (1 - 0.3 * rim)[..., None]
    lc = lerp3(hexlin('#848A72'), hexlin('#9A9E8C'), smoothstep(0.4, 0.7, kind)) * (0.8 + 0.35 * pnoise(h, w, 400, 120, seed + 13, 2))[..., None]
    col = lerp3(col, lc * 0.85, lichen * 0.6)
    col = lerp3(col, hexlin('#9C907C'), lent * 0.5)
    col = col * (1 - 0.7 * fiss)[..., None] * (0.9 + 0.2 * wr)[..., None]
    height = 0.6 + 0.1 * (wr - 0.5) + 0.05 * (fine - 0.5) - 0.3 * fiss
    height = height * (1 - dec) + (0.4 + 0.06 * (grain - 0.5)) * dec + 0.05 * rim + 0.06 * lichen + 0.05 * lent
    smooth = 0.12 + 0.03 * (fine - 0.5) + 0.04 * damp
    smooth = smooth + (0.16 - smooth) * dec
    smooth = smooth + (0.07 - smooth) * lichen
    return col, height, smooth, 0.0015


def tex_bare(h, w, seed):
    """Ободранная выветренная древесина: серебристо-серая, волокна вдоль (тонкие тёмные линии), редкие продольные
    трещины усушки, бурые и тёмные пятна плесени."""
    rng = np.random.default_rng(seed)
    x = (np.arange(w, dtype=np.float32) + 0.5) / w
    warp = (pnoise(h, w, 5, 3, seed + 1, 3) - 0.5) * 0.10 + (pnoise(h, w, 40, 6, seed + 2, 2) - 0.5) * 0.025
    n_lines = 34
    phase = (x[None, :] + warp) * n_lines
    lid = np.floor(phase).astype(int) % n_lines
    strength = rng.uniform(0.3, 1.0, n_lines)[lid]
    g = np.abs(phase - np.floor(phase) - 0.5) * 2
    lines = smoothstep(0.7, 1.0, g) * strength
    fine = pnoise(h, w, 160, 40, seed + 3, 2)
    big = pnoise(h, w, 3, 2, seed + 4, 3, 0.6)
    crn = pnoise(h, w, 3, 8, seed + 5, 3)
    checks = (1 - smoothstep(0.0, 0.018, np.abs(crn - 0.5))) * smoothstep(0.5, 0.7, pnoise(h, w, 4, 3, seed + 6, 2))
    mould = smoothstep(0.72, 0.85, pnoise(h, w, 24, 6, seed + 7, 4))
    brown = smoothstep(0.55, 0.8, pnoise(h, w, 4, 2, seed + 8, 3))
    col = lerp3(hexlin('#77716A'), hexlin('#959083'), np.clip(0.5 + (big - 0.5) * 1.6 + (fine - 0.5) * 0.4, 0, 1))
    col = lerp3(col, hexlin('#76695A'), brown * 0.6)
    col = col * (1 - 0.22 * lines)[..., None]
    col = col * (1 - 0.65 * checks)[..., None]
    col = lerp3(col, hexlin('#4C4842'), mould * 0.5)
    height = 0.5 - 0.12 * lines - 0.4 * checks + 0.05 * (fine - 0.5)
    smooth = 0.15 - 0.04 * mould
    return col, height, smooth, 0.0006


def tex_pine(h, w, seed):
    """Кора сухих сосновых веточек: рыже-бурая #7D5A3F, неровные листовые подушечки по спирали (~4 × 5 мм) с
    рубчиком, тонкие бумажные чешуйки отслаивающейся серой кожицы полосами вдоль, под ними — рыжая луб."""
    rng = np.random.default_rng(seed)
    cols, rows = 20, 96
    gx = (np.arange(w, dtype=np.float32) + 0.5) / w * cols
    gy = (np.arange(h, dtype=np.float32) + 0.5) / h * rows
    GX, GY = np.meshgrid(gx, gy)
    warpx = (pnoise(h, w, 24, 6, seed + 9, 2) - 0.5) * 0.6
    row = np.floor(GY).astype(int)
    GXs = GX + 0.5 * (row % 2) + warpx
    ci = np.floor(GXs).astype(int)
    jit = rng.random((rows, cols, 4)).astype(np.float32)
    jx = (jit[row % rows, ci % cols, 0] - 0.5) * 0.4
    jy = (jit[row % rows, ci % cols, 1] - 0.5) * 0.4
    sz = 0.75 + 0.5 * jit[row % rows, ci % cols, 3]
    fx = GXs - ci - 0.5 - jx
    fy = GY - row - 0.5 - jy
    d = (np.abs(fx) / 0.56 + np.abs(fy) / 0.62) / sz
    cushion = smoothstep(1.05, 0.25, d)
    scar = np.exp(-(fx ** 2 + (fy + 0.16) ** 2) / 0.008) * (jit[row % rows, ci % cols, 2] > 0.3)
    cell_b = 0.9 + 0.2 * jit[row % rows, ci % cols, 2]
    fine = pnoise(h, w, 128, 40, seed + 1, 2)
    big = pnoise(h, w, 4, 2, seed + 2, 3, 0.6)
    fl = pnoise(h, w, 10, 34, seed + 3, 4) + (big - 0.5) * 0.3
    t_f = float(np.quantile(fl, 0.5))
    flakes = smoothstep(t_f - 0.01, t_f + 0.01, fl)
    edge = smoothstep(t_f - 0.03, t_f - 0.005, fl) * (1 - flakes)
    col = lerp3(hexlin('#5A4232'), hexlin('#77583F'), cushion * 0.8 + 0.2) * (cell_b * (0.9 + 0.2 * fine))[..., None]
    col = lerp3(col, hexlin('#806450'), cushion * 0.15)
    grey = lerp3(hexlin('#6E6255'), hexlin('#8B7E6E'), fine)
    col = lerp3(col, grey, flakes * 0.85)
    col = col * (1 - 0.25 * edge)[..., None]
    col = col * (1 - 0.35 * scar)[..., None]
    height = 0.3 + 0.35 * cushion * (1 - flakes) - 0.1 * scar + 0.2 * flakes + 0.04 * (fine - 0.5)
    smooth = 0.16 + 0.06 * flakes
    return col, height, smooth, 0.0010


def tex_moss(h, w, seed):
    """Старая кора упавшей ветви со мхом: тёмная продольно-трещиноватая кора, ~50 % подушек мха (оливково-зелёный
    #5C6B3A, кончики светлее, подсохшие места желтоватые) с волокнистой фактурой."""
    lines, lid = line_field(h, w, 7, seed + 1, 0.2, 0.35)
    plates = pnoise(h, w, 8, 10, seed + 2, 3)
    bark_h = (1 - lines) * (0.7 + 0.3 * plates)
    mn = pnoise(h, w, 12, 9, seed + 3, 4, 0.6)
    t_m = float(np.quantile(mn, 0.45))
    moss = smoothstep(t_m - 0.025, t_m + 0.035, mn)
    fuzz = pnoise(h, w, 256, 64, seed + 4, 2, 0.6)
    clumps = pnoise(h, w, 80, 24, seed + 5, 3)
    dry = smoothstep(0.5, 0.75, pnoise(h, w, 3, 2, seed + 6, 3))
    bark_c = lerp3(hexlin('#3A332B'), hexlin('#625749'), bark_h)
    moss_c = lerp3(hexlin('#3E4529'), hexlin('#5C6B3A'), clumps)
    moss_c = lerp3(moss_c, hexlin('#727B45'), smoothstep(0.6, 0.85, fuzz) * 0.6)
    moss_c = lerp3(moss_c, hexlin('#76703F'), dry * 0.55)
    moss_c = moss_c * (0.82 + 0.3 * fuzz * clumps + 0.1)[..., None]
    col = lerp3(bark_c, moss_c, moss)
    height = 0.35 * bark_h * (1 - moss) + (0.5 + 0.3 * clumps + 0.1 * fuzz) * moss
    smooth = 0.10 * (1 - moss) + 0.04 * moss
    return col, height, smooth, 0.002


def _disc_coords(n):
    c = (np.arange(n, dtype=np.float32) + 0.5) / n * 2 - 1
    X, Y = np.meshgrid(c, -c)   # Y вверх по картинке
    return X, Y


def tex_end(n, seed, old=False):
    """Торец: годичные кольца (поздняя древесина темнее), сердцевина, клиновидные трещины усушки от коры внутрь
    (извилистые), кольцо коры на радиусе 0.42 блока (там лежат вершины кольца крышки). old — серый торец, трещин
    больше и они шире, одна сквозная до сердцевины."""
    rng = np.random.default_rng(seed)
    X, Y = _disc_coords(n)
    r = np.sqrt(X ** 2 + Y ** 2)
    ang = np.arctan2(Y, X)
    R = 0.84 * 0.94
    rn = r / R
    ai = ((ang + np.pi) / (2 * np.pi) * 512).astype(int) % 512
    wob = periodic_1d(512, seed + 1, freqs=(2, 3, 5, 7))[ai] * 0.05
    rw = rn * (1 + wob) + (pnoise(n, n, 6, 6, seed + 2, 3) - 0.5) * 0.04
    rings_n = rng.uniform(6, 11)
    ph = rw * rings_n
    fr = ph - np.floor(ph)
    late = smoothstep(0.72, 0.95, fr) * (1 - smoothstep(0.95, 1.0, fr))
    pith = 1 - smoothstep(0.03, 0.07, rn)
    checks = np.zeros_like(r)
    n_checks = rng.integers(4, 7) if old else rng.integers(1, 4)
    for k in range(n_checks):
        a0 = rng.uniform(-np.pi, np.pi)
        r_in = 0.05 if (old and k == 0) else rng.uniform(0.25, 0.75)
        w0 = rng.uniform(0.025, 0.05) * (1.5 if old else 1.0)
        bend = rng.uniform(-0.5, 0.5)
        wig = periodic_1d(256, seed + 10 + k, freqs=(3, 5, 9))
        ri = np.clip((rn * 255).astype(int), 0, 255)
        a = a0 + bend * (1 - rn) + 0.06 * wig[ri]
        da = np.abs((ang - a + np.pi) % (2 * np.pi) - np.pi)
        dist = r * np.sin(np.minimum(da, np.pi / 2))
        width = w0 * np.clip((rn - r_in) / max(1.0 - r_in, 0.05), 0, 1) ** 0.8 + 0.002
        on = (da < np.pi / 2) & (rn > r_in) & (rn < 1.03)
        checks = np.maximum(checks, on * (1 - smoothstep(width * 0.4, width, dist)))
    bark = smoothstep(0.98, 1.03, rw)
    fine = pnoise(n, n, 40, 40, seed + 3, 3)
    if old:
        wood = lerp3(hexlin('#77706A'), hexlin('#958E82'), smoothstep(0.0, 1.0, rn))
    else:
        wood = lerp3(hexlin('#A08460'), hexlin('#BCA37C'), smoothstep(0.3, 0.7, rn))
        wood = lerp3(wood, hexlin('#9A9080'), 0.3 * rn ** 2)
    col = wood * (1 - 0.2 * late)[..., None] * (0.92 + 0.16 * fine)[..., None]
    col = lerp3(col, hexlin('#5A4632' if not old else '#4F4A42'), pith)
    col = lerp3(col, hexlin('#2A231C'), checks * 0.9)
    bark_c = lerp3(hexlin('#463D33'), hexlin('#5F5445'), fine)
    col = lerp3(col, bark_c, bark)
    height = 0.55 - 0.06 * late - 0.45 * checks + 0.08 * bark + 0.03 * (fine - 0.5)
    smooth = np.full_like(r, 0.14 - 0.04 * float(old))
    return col, height, smooth, 0.0004


def tex_splinter(n, seed):
    """Расщеп обломанного конца: светлые волокна-щепки сходятся к острию (верх-центр блока), между ними тёмные щели,
    щепки разной длины — зубчатый край."""
    rng = np.random.default_rng(seed)
    X, Y = _disc_coords(n)
    yb = (Y + 1) * 0.5                       # 0 внизу блока .. 1 вверху
    q = X / np.maximum(1.0 - 0.94 * yb, 0.06)
    bounds = np.cumsum(rng.uniform(0.06, 0.2, 40))
    bounds = bounds - bounds[len(bounds) // 2] + rng.uniform(-0.05, 0.05)
    k = np.clip(np.searchsorted(bounds, q), 1, len(bounds) - 1)
    lo, hi = bounds[k - 1], bounds[k]
    f = (q - lo) / (hi - lo)
    edge = smoothstep(0.0, 0.18, f) * smoothstep(1.0, 0.82, f)
    ends = rng.uniform(0.55, 1.05, len(bounds))[k]
    alive = smoothstep(ends, ends - 0.08, yb)
    bright = rng.uniform(0.0, 1.0, len(bounds))[k]
    fib = pnoise(n, n, 4, 64, seed + 1, 3)
    wood = lerp3(hexlin('#8F7A5C'), hexlin('#C2A881'), bright * 0.7 + 0.3 * fib)
    wood = lerp3(wood, hexlin('#8F887C'), smoothstep(0.5, 1.0, yb) * 0.3)
    s = edge * alive
    col = lerp3(hexlin('#33291F'), wood, s)
    height = 0.15 + 0.7 * s + 0.08 * (fib - 0.5) * s
    smooth = 0.16 * s + 0.05
    return col, height, smooth, 0.0012


def tex_cone(n, seed, circ=0.11, length=0.046):
    """Чешуи шишки сосны обыкновенной: ромбические щитки (апофизы) ~6 мм по спиралям 8/13/21, пупок посередине,
    поперечный киль, тёмные щели раскрытой шишки. По U бесшовно (вокруг шишки), V — от основания к верхушке."""
    rng = np.random.default_rng(seed)
    m = 150
    k = np.arange(m)
    pu = (k * 0.6180339887 + rng.uniform(0, 1)) % 1.0 + rng.normal(0, 0.004, m)
    pv = (k + 0.5) / m + rng.normal(0, 0.0015, m)
    c = (np.arange(n, dtype=np.float32) + 0.5) / n
    U, V = np.meshgrid(c, c[::-1])
    f1 = np.full((n, n), 1e9, np.float32)
    f2 = np.full((n, n), 1e9, np.float32)
    idx = np.zeros((n, n), int)
    du1 = np.zeros((n, n), np.float32)
    dv1 = np.zeros((n, n), np.float32)
    for i in range(m):
        du = ((U - pu[i] + 0.5) % 1.0 - 0.5) * circ
        dv = (V - pv[i]) * length
        d = np.sqrt(du * du + dv * dv)
        closer = d < f1
        f2 = np.where(closer, f1, np.minimum(f2, d))
        f1 = np.where(closer, d, f1)
        idx = np.where(closer, i, idx)
        du1 = np.where(closer, du, du1)
        dv1 = np.where(closer, dv, dv1)
    e = f2 - f1
    plate = smoothstep(0.0003, 0.0024, e)
    umbo = np.exp(-(du1 ** 2 + dv1 ** 2) / (0.0007 ** 2))
    keel = np.exp(-(dv1 ** 2) / (0.0006 ** 2)) * plate
    lower = smoothstep(0.0, -0.0025, dv1) * (1 - smoothstep(0.0, 0.0022, e))   # тень под щитком (к основанию)
    upper = smoothstep(0.0, 0.0025, dv1) * (1 - smoothstep(0.0005, 0.0025, e)) * plate   # светлый край щитка
    rnd = rng.random(m).astype(np.float32)[idx]
    fine = pnoise(n, n, 32, 32, seed + 1, 3)
    col = lerp3(hexlin('#7C664E'), hexlin('#A08667'), np.clip(plate * (0.55 + 0.25 * keel) + 0.2 * rnd, 0, 1))
    col = col * (0.9 + 0.2 * fine)[..., None]
    col = lerp3(col, hexlin('#857560'), 0.2)
    col = lerp3(col, hexlin('#8B8274'), smoothstep(0.8, 0.95, rnd) * 0.35)
    col = lerp3(col, hexlin('#6A6255'), umbo * 0.6)
    col = lerp3(col, hexlin('#B09A7A'), upper * 0.45)
    col = lerp3(col, hexlin('#3C2F23'), np.clip((1 - plate) * 0.5 + lower * 0.5, 0, 1))
    height = 0.1 + 0.6 * plate + 0.12 * keel + 0.1 * umbo - 0.25 * lower + 0.1 * upper
    smooth = 0.12 + 0.1 * plate
    return col, height, smooth, 0.003


def build_wood_texture(out_dir):
    """ground_wood 1024²: столбцы коры и блоки торца/расщепа/шишки. Пишет albedo/normal/mask и layermask_none."""
    H = WOOD_PX
    albedo = np.zeros((H, H, 3), np.float32)
    normal = np.zeros((H, H, 3), np.float32)
    mask = np.zeros((H, H, 4), np.float32)
    stats = {}

    def put(y0, x0, col, hgt, sm, relief, texel, name):
        hh, ww = hgt.shape
        ao = cavity(hgt, radii=(2, 6, 14), gain=2.5)
        nrm = normal_from_height(hgt * relief, texel)
        albedo[y0:y0 + hh, x0:x0 + ww] = col * (0.84 + 0.16 * ao)[..., None]
        normal[y0:y0 + hh, x0:x0 + ww] = nrm
        mask[y0:y0 + hh, x0:x0 + ww, 0] = 0.0
        mask[y0:y0 + hh, x0:x0 + ww, 1] = ao
        mask[y0:y0 + hh, x0:x0 + ww, 2] = np.clip(hgt, 0, 1)
        mask[y0:y0 + hh, x0:x0 + ww, 3] = np.clip(sm, 0, 1)
        m = albedo[y0:y0 + hh, x0:x0 + ww].reshape(-1, 3).mean(0)
        srgb = np.where(m <= 0.0031308, m * 12.92, 1.055 * m ** (1 / 2.4) - 0.055)
        stats[name] = '#%02X%02X%02X' % tuple(int(round(c * 255)) for c in srgb)

    for i, (name, fn) in enumerate((('bark', tex_bark), ('bare', tex_bare), ('pine', tex_pine), ('moss', tex_moss))):
        x0, x1 = COLUMNS[name]
        col, hgt, sm, relief = fn(H, x1 - x0, 1000 + i * 37)
        put(0, x0, col, hgt, sm, relief, TEXEL_M, name)
    blocks = {'end': lambda: tex_end(256, 2001), 'splinter': lambda: tex_splinter(256, 2002),
              'cone': lambda: tex_cone(256, 2003), 'end_old': lambda: tex_end(256, 2004, old=True)}
    for k, name in enumerate(BLOCKS):
        col, hgt, sm, relief = blocks[name]()
        texel = 0.11 / 256 if name == 'cone' else 0.0002
        put(k * 256, 768, col, hgt, sm, relief, texel, name)
    os.makedirs(out_dir, exist_ok=True)
    eg.save_png(albedo, os.path.join(out_dir, 'ground_wood_albedo.png'))
    eg.save_png(normal, os.path.join(out_dir, 'ground_wood_normal.png'), linear=True)
    eg.save_png(mask, os.path.join(out_dir, 'ground_wood_mask.png'), linear=True)
    eg.save_png(np.zeros((4, 4, 4), np.float32), os.path.join(out_dir, 'layermask_none.png'), linear=True)
    print('GROUND_WOOD_TEX', json.dumps(stats))
    return stats


# ------------------------------------------------------------------ сборщик меша (нормали на углах)

class GMesh:
    """Как envgen_common.MeshBuilder, но у каждого угла своя нормаль; цвет вершин (1, 1, 1, 0) на всех углах."""

    def __init__(self):
        self.verts = []
        self.faces = []   # (indices, uv0, uv1, normals)

    def vert(self, co):
        self.verts.append(tuple(co))
        return len(self.verts) - 1

    def face(self, idx, uv0, normals, uv1=None):
        n = len(idx)
        self.faces.append((tuple(idx), list(uv0), list(uv1 or [(0.0, 0.0)] * n), [Vector(v).normalized() for v in normals]))

    @property
    def triangles(self):
        return sum(len(f[0]) - 2 for f in self.faces)

    def to_object(self, name, material):
        mesh = bpy.data.meshes.new(name)
        bm = bmesh.new()
        bverts = [bm.verts.new(v) for v in self.verts]
        bm.verts.ensure_lookup_table()
        uv0 = bm.loops.layers.uv.new('UVMap')
        uv1 = bm.loops.layers.uv.new('UV2')
        normals = []
        skipped = 0
        for idx, a, b, nrm in self.faces:
            try:
                f = bm.faces.new([bverts[i] for i in idx])
            except ValueError:
                skipped += 1
                continue
            for loop, ua, ub in zip(f.loops, a, b):
                loop[uv0].uv = ua
                loop[uv1].uv = ub
            normals.extend(nrm)
        bm.to_mesh(mesh)
        bm.free()
        if skipped:
            print('GROUND_WARN skipped faces', name, skipped)
        col = mesh.color_attributes.new(name='Col', type='BYTE_COLOR', domain='CORNER')
        for d in col.data:
            d.color = (1.0, 1.0, 1.0, 0.0)
        mesh.materials.append(material)
        for p in mesh.polygons:
            p.use_smooth = True
        mesh.normals_split_custom_set(normals)
        obj = bpy.data.objects.new(name, mesh)
        eg.link(obj)
        return obj


def placeholder_material(name):
    m = bpy.data.materials.get(name)
    if m is None:
        m = bpy.data.materials.new(name)
    return m


# ------------------------------------------------------------------ трубки веток

def frames_along(pts):
    """Параллельный перенос рамки; начальная нормаль — вниз (шов развёртки и стык цилиндра лежат в земле)."""
    n = len(pts)
    out = []
    prev = None
    for i in range(n):
        if i == 0:
            t = (pts[1] - pts[0]).normalized()
        elif i == n - 1:
            t = (pts[-1] - pts[-2]).normalized()
        else:
            t = ((pts[i + 1] - pts[i]).normalized() + (pts[i] - pts[i - 1]).normalized()).normalized()
        if prev is None:
            nrm = DOWN - t * DOWN.dot(t)
            if nrm.length < 1e-4:
                nrm = t.orthogonal()
            nrm.normalize()
        else:
            nrm = (prev - t * prev.dot(t))
            if nrm.length < 1e-6:
                nrm = t.orthogonal()
            nrm.normalize()
        prev = nrm
        out.append((t, nrm, t.cross(nrm).normalized()))
    return out


class Piece:
    """Трубка: ось (3D-точки), радиусы, число граней, столбец коры, концы ('broken', 'snap', 'tip', 'open')."""

    def __init__(self, pts, radii, sides, region, start='open', end='broken', end_block='end'):
        self.pts = [Vector(p) for p in pts]
        self.radii = list(radii)
        self.sides = sides
        self.region = region
        self.start = start
        self.end = end
        self.end_block = end_block


def emit_piece(m, piece, rng):
    pts, radii, sides = piece.pts, piece.radii, piece.sides
    frames = frames_along(pts)
    u_lo, u_hi = column_range(piece.region)
    width = u_hi - u_lo
    circ = 2 * math.pi * max(radii[0], 0.001)
    span = circ * UV_PER_M
    k = 1.0
    if span > width:
        k = width / span
        span = width
    u0 = u_lo + rng.uniform(0, width - span)
    v = rng.uniform(0, 1)
    total = sum((pts[i + 1] - pts[i]).length for i in range(len(pts) - 1)) or 1.0
    acc = 0.0
    rings = []
    # не труба: лёгкая овальность сечения, неровность радиуса по вершинам, ось чуть гуляет между узлами
    oval = rng.uniform(0.0, 0.09)
    oval_phase = rng.uniform(0, math.pi)
    for i, p in enumerate(pts):
        if i:
            acc += (pts[i] - pts[i - 1]).length
        t, nx, ny = frames[i]
        r = max(radii[i], 0.0012)
        vv = v + acc * UV_PER_M * k
        inner = 0 < i < len(pts) - 1
        shift = (nx * rng.uniform(-0.05, 0.05) + ny * rng.uniform(-0.05, 0.05)) * r if inner else Vector()
        ring = []
        for s in range(sides + 1):
            a = s / sides * math.tau
            d = nx * math.cos(a) + ny * math.sin(a)
            rr = r * (1 + oval * math.cos(2 * a + oval_phase)) * (rng.uniform(0.94, 1.06) if s < sides else 1.0)
            pos = p + shift + d * rr
            idx = m.vert(pos) if s < sides else ring[0][0]
            ring.append((idx, (u0 + span * s / sides, vv), d, pos if s < sides else ring[0][3]))
        rings.append((ring, acc / total))
    for i in range(len(rings) - 1):
        ra, ta = rings[i]
        rb, tb = rings[i + 1]
        for s in range(sides):
            a, b, c, d = ra[s], ra[s + 1], rb[s + 1], rb[s]
            m.face([a[0], b[0], c[0], d[0]], [a[1], b[1], c[1], d[1]], [a[2], b[2], c[2], d[2]],
                   [(ta, 0), (ta, 0), (tb, 0), (tb, 0)])
    vend = rings[-1][0][0][1][1]
    for which in ('start', 'end'):
        kind = getattr(piece, which)
        if kind == 'open':
            continue
        if which == 'end':
            ring, centre, (t, nx, ny), r, outward = rings[-1][0], pts[-1], frames[-1], radii[-1], 1
        else:
            ring, centre, (t, nx, ny), r, outward = rings[0][0], pts[0], frames[0], radii[0], -1
        t = t * outward
        ring = ring[:sides]
        if outward < 0:
            ring = ring[::-1]
            ny = -ny        # базис (nx, ny, t) правый для внешней стороны
        if kind == 'tip':
            tip_cap(m, ring, centre, t, r, rng, u0, span, vend if which == 'end' else v, k)
        else:
            broken_cap(m, ring, centre, t, nx, ny, r, rng, kind, piece.end_block)


def tip_cap(m, ring, centre, t, r, rng, u0, span, vend, k):
    """Естественный кончик веточки: конус на почку длиной 1.5–3 r."""
    length = rng.uniform(1.5, 3.0) * r
    apex = m.vert(centre + t * length)
    n = len(ring)
    for s in range(n):
        a, b = ring[s], ring[(s + 1) % n]
        ua = a[1][0]
        ub = b[1][0] if (s + 1) % n else u0 + span
        m.face([a[0], b[0], apex], [a[1], (ub, b[1][1]), ((ua + ub) * 0.5, vend + length * UV_PER_M * k)],
               [a[2] * 0.8 + t * 0.4, b[2] * 0.8 + t * 0.4, t])


def broken_cap(m, ring, centre, t, nx, ny, r, rng, kind, block):
    """Обломанный конец: 2–3 зубца на торце. 'broken' — длинный расщеп (блок волокон), 'snap' — почти ровный слом
    (блок торца с кольцами). ring — вершины по возрастанию угла вокруг внешней оси t (против часовой снаружи)."""
    n = len(ring)
    nsp = 2 if n <= 4 else rng.choice([2, 3])
    base = rng.uniform(0, math.tau)
    spikes = []
    for j in range(nsp):
        a = base + math.tau * j / nsp + rng.uniform(-0.35, 0.35)
        off = rng.uniform(0.12, 0.45) * r
        if kind == 'broken':
            hgt = (rng.uniform(0.9, 2.2) if j == 0 else rng.uniform(0.15, 0.7)) * r
        else:
            hgt = rng.uniform(0.04, 0.3) * r
        p = centre + (nx * math.cos(a) + ny * math.sin(a)) * off + t * hgt
        spikes.append((m.vert(p), a % math.tau, p))
    spikes.sort(key=lambda s: s[1])

    def ang_of(pos):
        d = pos - centre
        return math.atan2(d.dot(ny), d.dot(nx)) % math.tau

    def nearest(a):
        best, bd = 0, 9.0
        for j, s in enumerate(spikes):
            dd = abs((a - s[1] + math.pi) % math.tau - math.pi)
            if dd < bd:
                best, bd = j, dd
        return best

    ring_ang = [ang_of(rv[3]) for rv in ring]
    assign = [nearest(a) for a in ring_ang]

    def end_uv(pos):
        d = pos - centre
        return block_uv(block if kind == 'snap' else 'end', 0.5 + 0.42 * d.dot(nx) / r, 0.5 + 0.42 * d.dot(ny) / r)

    def fnorm(p0, p1, p2):
        fn = (p1 - p0).cross(p2 - p0)
        return (fn.normalized() if fn.length > 1e-12 else t) * 0.7 + t * 0.3

    tris = []
    for i in range(n):
        j = (i + 1) % n
        sa, sb = assign[i], assign[j]
        tris.append(('fan', ring[i], ring[j], spikes[sa]))
        if sa != sb:
            tris.append(('bridge', ring[j], spikes[sb], spikes[sa]))
    if nsp == 3:
        tris.append(('core', spikes[0], spikes[1], spikes[2]))
    for kind_t, a, b, c in tris:
        pa = a[3] if len(a) == 4 else a[2]
        pb = b[3] if len(b) == 4 else b[2]
        pc = c[3] if len(c) == 4 else c[2]
        nrm = fnorm(pa, pb, pc)
        if kind == 'snap':
            uvs = [end_uv(pa), end_uv(pb), end_uv(pc)]
        elif kind_t == 'fan':
            da = ((ang_of(pa) - c[1] + math.pi) % math.tau - math.pi) / math.pi
            db = ((ang_of(pb) - c[1] + math.pi) % math.tau - math.pi) / math.pi
            if db < da:
                db += 2.0
            uvs = [block_uv('splinter', 0.5 + 0.45 * da, 0.03), block_uv('splinter', 0.5 + 0.45 * min(db, 1.0), 0.03),
                   block_uv('splinter', 0.5, 0.97)]
        elif kind_t == 'bridge':
            uvs = [block_uv('splinter', 0.5, 0.05), block_uv('splinter', 0.62, 0.9), block_uv('splinter', 0.38, 0.9)]
        else:
            uvs = [block_uv('splinter', 0.4, 0.9), block_uv('splinter', 0.6, 0.9), block_uv('splinter', 0.5, 0.97)]
        m.face([a[0], b[0], c[0]], uvs, [nrm, nrm, nrm])


def stub_cone(m, base, direction, r, length, region, rng):
    """Пенёк обломанной боковой веточки: трёхгранный конус из оси родителя (основание спрятано в нём)."""
    d = direction.normalized()
    nx = (DOWN - d * DOWN.dot(d))
    nx = nx.normalized() if nx.length > 1e-4 else d.orthogonal().normalized()
    ny = d.cross(nx).normalized()
    u_lo, u_hi = column_range(region)
    span = min(u_hi - u_lo, 2 * math.pi * r * UV_PER_M)
    u0 = u_lo + rng.uniform(0, (u_hi - u_lo) - span)
    v0 = rng.uniform(0, 1)
    ring = []
    for s in range(3):
        a = s / 3 * math.tau
        dd = nx * math.cos(a) + ny * math.sin(a)
        ring.append((m.vert(base + dd * r), dd, u0 + span * s / 3))
    apex = m.vert(base + d * length)
    for s in range(3):
        a, b = ring[s], ring[(s + 1) % 3]
        ub = b[2] if s < 2 else u0 + span
        m.face([a[0], b[0], apex], [(a[2], v0), (ub, v0), ((a[2] + ub) * 0.5, v0 + length * UV_PER_M)],
               [a[1] * 0.85 + d * 0.3, b[1] * 0.85 + d * 0.3, d])


# ------------------------------------------------------------------ раскладка веток на земле (2D-проверки)

def _v2(p):
    return Vector((p.x, p.y))


def seg_intersect(p, p2, q, q2):
    r = p2 - p
    s = q2 - q
    den = r.x * s.y - r.y * s.x
    if abs(den) < 1e-12:
        return None
    qp = q - p
    t = (qp.x * s.y - qp.y * s.x) / den
    u = (qp.x * r.y - qp.y * r.x) / den
    if 0.0 <= t <= 1.0 and 0.0 <= u <= 1.0:
        return t, u
    return None


def point_seg(pt, a, b):
    ab = b - a
    L = ab.length_squared
    t = 0.0 if L < 1e-12 else max(0.0, min(1.0, (pt - a).dot(ab) / L))
    return (pt - (a + ab * t)).length, t


def seg_dist(p, p2, q, q2):
    best = (9e9, None)
    for pt, a, b, tag in ((p, q, q2, 'q'), (p2, q, q2, 'q'), (q, p, p2, 'p'), (q2, p, p2, 'p')):
        d, t = point_seg(pt, a, b)
        if d < best[0]:
            best = (d, pt if tag == 'q' else a + (b - a) * t)
    return best


class Layout:
    """Уже лежащие на земле звенья (2D-ось, высота и радиус концов) — чтобы новые не проходили сквозь них."""

    def __init__(self):
        self.segs = []   # (a2, b2, za, zb, ra, rb, owner)

    def add(self, pts, radii, owner):
        for i in range(len(pts) - 1):
            self.segs.append((_v2(pts[i]), _v2(pts[i + 1]), pts[i].z, pts[i + 1].z, radii[i], radii[i + 1], owner))

    def check(self, path, radii, skip=(), cross_r=0.0075, max_cross=2, min_angle=30.0):
        """path: 2D-точки. Возвращает (ok, [(i, t, z_над, точка)]) — пересечения, над которыми звено приподнять."""
        crossings = []
        near = []
        for i in range(len(path) - 1):
            a, b = path[i], path[i + 1]
            for (q, q2, za, zb, ra, rb, owner) in self.segs:
                if owner in skip:
                    continue
                hit = seg_intersect(a, b, q, q2)
                if hit:
                    t, u = hit
                    r_new = radii[i] + (radii[i + 1] - radii[i]) * t
                    r_old = ra + (rb - ra) * u
                    da, db = (b - a).normalized(), (q2 - q).normalized()
                    ang = math.degrees(math.acos(min(1.0, abs(da.dot(db)))))
                    if r_old > cross_r or ang < min_angle:
                        return False, []
                    z_old = za + (zb - za) * u
                    crossings.append((i, t, z_old + r_old + 0.85 * r_new, a + (b - a) * t))
                    continue
                d, at = seg_dist(a, b, q, q2)
                if d < max(radii[i], radii[i + 1]) + max(ra, rb) + 0.003:
                    near.append(at)
        if len(crossings) > max_cross:
            return False, []
        for at in near:
            if not any((at - c[3]).length < 0.035 for c in crossings):
                return False, []
        return True, crossings


def lay_nodes(path2d, radii, crossings, rng, sink=0.0, z_factor=0.7):
    """3D-узлы на земле: центр на z_factor·r (минус sink), над пересечениями — вставленный приподнятый узел."""
    nodes, rr = [], []
    by_seg = {}
    for i, t, z, _ in crossings:
        by_seg.setdefault(i, []).append((t, z))
    for i, p in enumerate(path2d):
        r = radii[i]
        nodes.append(Vector((p.x, p.y, z_factor * r - sink + rng.uniform(-0.05, 0.22) * r)))
        rr.append(r)
        if i < len(path2d) - 1 and i in by_seg:
            for t, z in sorted(by_seg[i]):
                t = min(max(t, 0.08), 0.92)
                q = p + (path2d[i + 1] - p) * t
                r_q = r + (radii[i + 1] - r) * t
                nodes.append(Vector((q.x, q.y, z)))
                rr.append(r_q)
    return nodes, rr


def zigzag_path(rng, start, heading, length, nseg, zz_deg=15.0):
    pts = [Vector(start)]
    h = heading
    sign = rng.choice((-1, 1))
    lens = [rng.uniform(0.75, 1.25) for _ in range(nseg)]
    total = sum(lens)
    for i in range(nseg):
        if i:
            h += sign * math.radians(rng.uniform(4.0, zz_deg))
            sign = -sign
        pts.append(pts[-1] + Vector((math.cos(h), math.sin(h))) * (length * lens[i] / total))
    return pts


def path_radii(path, r0, r1):
    cum = [0.0]
    for i in range(len(path) - 1):
        cum.append(cum[-1] + (path[i + 1] - path[i]).length)
    L = cum[-1] or 1.0
    return [r0 + (r1 - r0) * c / L for c in cum], cum


def heading_at(path, i):
    i = min(i, len(path) - 2)
    d = path[i + 1] - path[i]
    return math.atan2(d.y, d.x)


def sides_for(r):
    return 4 if 2 * r >= 0.008 else 3


# ------------------------------------------------------------------ пятна веточек (детали)

TWIGS = {
    # дубовый лес: 6–9 веточек 0.08–0.45 м, Ø 4–15 мм; одна палка 0.5–0.9 м, Ø 15–30 мм
    'ground_twigs_01': dict(twigs=(6, 9), sticks=['bark'], region='bark', budget=450),
    # 5 веточек + 2 палки, 30 % без коры (одна палка и одна веточка — серая древесина)
    'ground_twigs_02': dict(twigs=(5, 5), sticks=['bark', 'bare'], bare_twigs=1, region='bark', budget=450),
    # сосновая подстилка: 6 тонких сосновых веточек с мутовками + 2–3 шишки
    'ground_twigs_03': dict(twigs=(6, 6), sticks=[], region='pine', cones=(2, 3), pine=True, budget=450),
}


def place_twig(rng, lay, owner, cfg, trim, region, kind, pieces, stubs):
    """Одна веточка (или палка) с вилками и пеньками; проверка по раскладке, до 400 попыток. Добавляет Piece."""
    pine = cfg.get('pine', False)
    for attempt in range(400):
        if kind == 'stick':
            length = rng.uniform(0.5, 0.9)
            r0 = rng.uniform(0.0075, 0.015)
            nseg = rng.choice((4, 5))
            sides = 6
            place_r = 0.12
        else:
            u = rng.random()
            if pine:
                r0 = 0.0018 + 0.0024 * u
                length = min(0.38, (0.12 + 0.18 * u) * rng.uniform(0.75, 1.3))
            else:
                r0 = 0.002 + 0.0055 * u ** 1.6
                length = min(0.45, (0.1 + 0.26 * u) * rng.uniform(0.65, 1.35))
            nseg = 3 if length > 0.16 else 2
            sides = sides_for(r0)
            place_r = 0.42
        rr = place_r * math.sqrt(rng.random())
        aa = rng.uniform(0, math.tau)
        centre = Vector((rr * math.cos(aa), rr * math.sin(aa)))
        heading = rng.uniform(0, math.tau)
        start = centre - Vector((math.cos(heading), math.sin(heading))) * length * 0.5
        path = zigzag_path(rng, start, heading, length, nseg)
        if any(max(abs(p.x), abs(p.y)) > 0.66 for p in path):
            continue
        r1 = r0 * rng.uniform(0.45, 0.7)
        radii, cum = path_radii(path, r0, r1)
        for i in range(1, len(radii) - 1):
            radii[i] *= rng.uniform(1.06, 1.16)     # утолщения узлов
        ok, crossings = lay.check(path, radii, cross_r=0.0075 if kind != 'stick' else 0.0)
        if not ok:
            continue
        # вилки (сосна: мутовка 2–3 побегов в одном узле)
        forks = []
        if kind == 'stick':
            n_forks = 0
        elif length > 0.16:
            n_forks = rng.choices((0, 1, 2), (0.2, 0.45, 0.35))[0]
        else:
            n_forks = rng.choices((0, 1), (0.55, 0.45))[0]
        if pine:
            n_forks = rng.randint(1, 2)
        n_forks = max(0, n_forks - trim)
        fork_ok = True
        inner = list(range(1, len(path) - 1))
        for fi in range(n_forks):
            if inner and rng.random() < 0.7:
                j = rng.choice(inner)
                base2, r_at, hdg = path[j], radii[j], heading_at(path, j)
            else:
                j = rng.randrange(len(path) - 1)
                t_at = rng.uniform(0.35, 0.7)
                base2 = path[j] + (path[j + 1] - path[j]) * t_at
                r_at = radii[j] + (radii[j + 1] - radii[j]) * t_at
                hdg = heading_at(path, j)
            count = rng.choice((2, 3)) if pine else 1
            side = rng.choice((-1, 1))
            for c in range(count):
                ang = math.radians(rng.uniform(40, 70) if pine else rng.uniform(25, 60)) * side
                side = -side
                fh = hdg + ang + (math.radians(rng.uniform(-15, 15)) if c == 2 else 0.0)
                flen = length * (rng.uniform(0.15, 0.35) if pine else rng.uniform(0.25, 0.55))
                fr0 = max(0.0012, r_at * rng.uniform(0.55, 0.8))
                fns = 2 if flen > 0.12 else 1
                fpath = zigzag_path(rng, base2, fh, flen, fns, 12.0)
                if any(max(abs(p.x), abs(p.y)) > 0.68 for p in fpath):
                    fork_ok = False
                    break
                frad, _ = path_radii(fpath, fr0, fr0 * rng.uniform(0.5, 0.75))
                # проверка без первых 30 % (там вилка выходит из родителя)
                head = fpath[0] + (fpath[1] - fpath[0]) * 0.3
                okf, fcross = lay.check([head] + fpath[1:], [frad[0]] + frad[1:], cross_r=0.0075, max_cross=1)
                if not okf or fcross:
                    fork_ok = False
                    break
                forks.append((fpath, frad, r_at))
            if not fork_ok:
                break
        if not fork_ok:
            continue
        nodes, nr = lay_nodes(path, radii, crossings, rng)
        lay.add(nodes, nr, owner)
        thick_end = 'snap' if (kind == 'stick' and rng.random() < 0.5) or region == 'bare' else 'broken'
        end_block = 'end_old' if region in ('bare', 'moss') or rng.random() < 0.4 else 'end'
        if kind == 'stick':
            thin_end = rng.choice(('broken', 'snap'))
        else:
            thin_end = 'tip' if rng.random() < (0.75 if pine else 0.6) else 'broken'
        pieces.append(Piece(nodes, nr, sides, region, start=thick_end, end=thin_end, end_block=end_block))
        for fpath, frad, r_at in forks:
            fnodes = [Vector((fpath[0].x, fpath[0].y, 0.7 * r_at))]
            for p, r in zip(fpath[1:], frad[1:]):
                fnodes.append(Vector((p.x, p.y, 0.7 * r + rng.uniform(-0.05, 0.1) * r)))
            fend = 'tip' if rng.random() < (0.7 if pine else 0.55) else 'broken'
            pieces.append(Piece(fnodes, frad, sides_for(frad[0]), region, start='open', end=fend, end_block=end_block))
            lay.add(fnodes, frad, owner)
        # пеньки обломанных боковых веточек
        n_stubs = max(0, (rng.randint(0, 2) if kind != 'stick' else rng.randint(1, 3)) - trim)
        for s in range(n_stubs):
            i = rng.randrange(len(nodes) - 1)
            t = rng.uniform(0.2, 0.8)
            base = nodes[i].lerp(nodes[i + 1], t)
            r_at = nr[i] + (nr[i + 1] - nr[i]) * t
            if r_at < 0.0022:
                continue
            seg = nodes[i + 1] - nodes[i]
            hdg = math.atan2(seg.y, seg.x) + math.radians(rng.uniform(40, 85)) * rng.choice((-1, 1))
            el = math.radians(rng.uniform(10, 40))
            d = Vector((math.cos(hdg) * math.cos(el), math.sin(hdg) * math.cos(el), math.sin(el)))
            stubs.append((base, d, r_at * 0.75, max(0.004, r_at * rng.uniform(1.6, 3.0)), region))
        return True
    return False


def cone_mesh(m, rng, centre, heading, length, radius):
    """Шишка сосны (раскрытая, 4–5 см): 6 граней × 4 кольца с поворотом и разбросом радиуса (зубчатый силуэт чешуй),
    основание тупое, верхушка острее; ось почти горизонтальна. UV — блок чешуй (по U бесшовно)."""
    prof = [(0.0, 0.0), (0.1, 0.72), (0.32, 1.0), (0.6, 0.84), (0.85, 0.45), (1.0, 0.0)]
    sides = 6
    tilt = math.radians(rng.uniform(-8, 4))
    axis = Vector((math.cos(heading) * math.cos(tilt), math.sin(heading) * math.cos(tilt), math.sin(tilt)))
    bend = Vector((-math.sin(heading), math.cos(heading), 0)) * rng.uniform(-0.06, 0.06)
    nx = (DOWN - axis * DOWN.dot(axis)).normalized()
    ny = axis.cross(nx).normalized()
    base = centre - axis * length * 0.45
    twist0 = rng.uniform(0, math.tau)
    rings = []
    for k, (t, f) in enumerate(prof):
        c = base + axis * length * t + bend * length * (t * (1 - t) * 4)
        if f == 0.0:
            rings.append(('apex', m.vert(c), t))
            continue
        ring = []
        tw = twist0 + k * math.radians(30)
        for s in range(sides + 1):
            a = tw + s / sides * math.tau
            jitter = rng.uniform(0.82, 1.16) if s < sides else 1.0
            d = nx * math.cos(a) + ny * math.sin(a)
            idx = m.vert(c + d * radius * f * jitter) if s < sides else ring[0][0]
            slope = prof[min(k + 1, 5)][1] - prof[max(k - 1, 0)][1]
            nrm = (d - axis * slope * 0.8).normalized()
            ring.append((idx, block_uv('cone', s / sides, 0.04 + 0.92 * t), nrm))
        rings.append(('ring', ring, t))
    for k in range(len(rings) - 1):
        ka, a, ta = rings[k]
        kb, b, tb = rings[k + 1]
        for s in range(sides):
            if ka == 'apex':
                q1, q2 = b[s], b[s + 1]
                m.face([a, q2[0], q1[0]], [block_uv('cone', (s + 0.5) / sides, 0.02), q2[1], q1[1]], [-axis, q2[2], q1[2]])
            elif kb == 'apex':
                q1, q2 = a[s], a[s + 1]
                m.face([q1[0], q2[0], b], [q1[1], q2[1], block_uv('cone', (s + 0.5) / sides, 0.98)], [q1[2], q2[2], axis])
            else:
                m.face([a[s][0], a[s + 1][0], b[s + 1][0], b[s][0]], [a[s][1], a[s + 1][1], b[s + 1][1], b[s][1]],
                       [a[s][2], a[s + 1][2], b[s + 1][2], b[s][2]])


def build_twigs(name, trim=0):
    cfg = TWIGS[name]
    rng = random.Random(zlib.crc32(name.encode()) + 7919 * trim)
    m = GMesh()
    lay = Layout()
    pieces, stubs = [], []
    owner = 0
    for region in cfg['sticks']:
        if place_twig(rng, lay, owner, cfg, trim, region, 'stick', pieces, stubs):
            owner += 1
    n = rng.randint(*cfg['twigs'])
    bare_left = cfg.get('bare_twigs', 0)
    placed = 0
    for i in range(n):
        region = cfg['region']
        if bare_left and (rng.random() < 0.5 or i == n - 1):
            region = 'bare'
            bare_left -= 1
        if place_twig(rng, lay, owner, cfg, trim, region, 'twig', pieces, stubs):
            owner += 1
            placed += 1
    cones = []
    if cfg.get('cones'):
        nc = rng.randint(*cfg['cones'])
        for c in range(nc):
            for attempt in range(300):
                rr = 0.4 * math.sqrt(rng.random())
                aa = rng.uniform(0, math.tau)
                centre2 = Vector((rr * math.cos(aa), rr * math.sin(aa)))
                L = rng.uniform(0.04, 0.05)
                R = L * rng.uniform(0.31, 0.36)
                hd = rng.uniform(0, math.tau)
                a2 = centre2 - Vector((math.cos(hd), math.sin(hd))) * L * 0.5
                b2 = centre2 + Vector((math.cos(hd), math.sin(hd))) * L * 0.5
                if any(seg_dist(a2, b2, q, q2)[0] < R + max(ra, rb) + 0.004 for q, q2, za, zb, ra, rb, o in lay.segs):
                    continue
                if any(seg_intersect(a2, b2, q, q2) for q, q2, za, zb, ra, rb, o in lay.segs):
                    continue
                cones.append((Vector((centre2.x, centre2.y, 0.8 * R)), hd, L, R))
                lay.add([Vector((a2.x, a2.y, R)), Vector((b2.x, b2.y, R))], [R, R], owner)
                owner += 1
                break
    for p in pieces:
        emit_piece(m, p, rng)
    for base, d, r, length, region in stubs:
        stub_cone(m, base, d, r, length, region, rng)
    for centre, hd, L, R in cones:
        cone_mesh(m, rng, centre, hd, L, R)
    return m, dict(twigs=placed, sticks=len(cfg['sticks']), cones=len(cones), pieces=len(pieces), stubs=len(stubs))


# ------------------------------------------------------------------ упавшие ветви (объекты точки взлёта)

BRANCHES = {
    # дуб: 2.2 м, комель Ø 6.8 см, 4 вилки с веточками, пара веточек у вершины
    'ground_branch_01': dict(length=2.2, r0=0.034, forks=4, sides=7, region='bark', fork_region='bark',
                             twig_region='bark', twigs_per_fork=(1, 3), tip_twigs=3, raised=0.45, whorl=False),
    # сосна: 2.7 м, Ø 6 см, боковые ветви мутовками почти поперёк оси, сосновая кора
    'ground_branch_02': dict(length=2.7, r0=0.03, forks=4, sides=6, region='pine', fork_region='pine',
                             twig_region='pine', twigs_per_fork=(1, 2), tip_twigs=3, raised=0.4, whorl=True),
    # старая ветвь: 3.0 м, Ø 9 см, кора со мхом, вилки ободраны, веточек мало (опали)
    'ground_branch_03': dict(length=3.0, r0=0.045, forks=5, sides=8, region='moss', fork_region='bare',
                             twig_region='bark', twigs_per_fork=(0, 2), tip_twigs=1, raised=0.5, whorl=False),
}


def path3d(rng, start, heading, elev, length, nseg, droop=0.25, wobble=8.0):
    """Ось поднятой ветви: стартует под углом elev, клонится под весом (droop рад на длину), лёгкие изломы."""
    pts = [Vector(start)]
    h, e = heading, elev
    step = length / nseg
    for i in range(nseg):
        h += math.radians(rng.uniform(-wobble, wobble))
        e -= droop / nseg + math.radians(rng.uniform(-wobble, wobble)) * 0.5
        d = Vector((math.cos(h) * math.cos(e), math.sin(h) * math.cos(e), math.sin(e)))
        pts.append(pts[-1] + d * step)
    return pts


def add_twig(rng, pieces, host_nodes, host_radii, raised, region, r_max=0.007):
    """Веточка на ветви: от точки на оси хозяина под 30–65°, 2–3 звена с изломами, лежит или торчит вверх."""
    if len(host_nodes) < 2:
        return
    i = rng.randrange(len(host_nodes) - 1)
    t = rng.uniform(0.25, 0.9)
    base = host_nodes[i].lerp(host_nodes[i + 1], t)
    r_at = host_radii[i] + (host_radii[i + 1] - host_radii[i]) * t
    seg = host_nodes[i + 1] - host_nodes[i]
    hdg = math.atan2(seg.y, seg.x) + math.radians(rng.uniform(30, 65)) * rng.choice((-1, 1))
    tr0 = max(0.0025, min(r_at * 0.7, rng.uniform(0.003, r_max)))
    tlen = rng.uniform(0.12, 0.4)
    nseg = 3 if tlen > 0.25 else 2
    up = raised or rng.random() < 0.25
    el = math.radians(rng.uniform(8, 32)) if up else math.radians(rng.uniform(-8, -2))
    tn = path3d(rng, base, hdg, el, tlen, nseg, droop=0.25 if up else 0.0, wobble=13.0)
    trad, _ = path_radii([_v2(p) for p in tn], tr0, tr0 * rng.uniform(0.4, 0.6))
    for p, r in zip(tn[1:], trad[1:]):
        p.z = max(p.z, 0.7 * r)
    pieces.append(Piece(tn, trad, sides_for(tr0), region, start='open', end='tip' if rng.random() < 0.6 else 'broken',
                        end_block='end_old'))


def build_branch(name, trim=0):
    cfg = BRANCHES[name]
    rng = random.Random(zlib.crc32(name.encode()) + 7919 * trim)
    m = GMesh()
    lay = Layout()
    pieces, stubs = [], []
    L = cfg['length']
    sink = rng.uniform(0.01, 0.02)
    nseg = max(6, round(L / 0.27))
    # главная ось: плавный изгиб + 2 излома
    h = rng.uniform(-0.15, 0.15)
    path = [Vector((-L * 0.5, 0.0))]
    kinks = set(rng.sample(range(1, nseg), 2))
    bend = rng.choice((-1, 1)) * math.radians(rng.uniform(0.5, 2.5))
    for i in range(nseg):
        h += bend + math.radians(rng.uniform(-3, 3) + (rng.choice((-1, 1)) * rng.uniform(5, 10) if i in kinks else 0))
        path.append(path[-1] + Vector((math.cos(h), math.sin(h))) * (L / nseg))
    r_tip = cfg['r0'] * rng.uniform(0.3, 0.4)
    radii, cum = path_radii(path, cfg['r0'], r_tip)
    for i in range(1, len(radii) - 1):
        radii[i] *= rng.uniform(0.97, 1.06)
    nodes = [Vector((p.x, p.y, r - sink + rng.uniform(-0.003, 0.004))) for p, r in zip(path, radii)]
    pieces.append(Piece(nodes, radii, cfg['sides'], cfg['region'], start='broken', end=rng.choice(('broken', 'snap')),
                        end_block='end_old'))
    lay.add(nodes, radii, 0)
    n_forks = cfg['forks']
    ts = sorted(rng.uniform(0.15, 0.85) for _ in range(n_forks))
    side = rng.choice((-1, 1))
    hosts = []
    for k, t in enumerate(ts):
        i = min(int(t * nseg), nseg - 1)
        f = t * nseg - i
        base = nodes[i].lerp(nodes[i + 1], f)
        r_at = radii[i] + (radii[i + 1] - radii[i]) * f
        hdg = heading_at(path, i)
        count = rng.choice((2, 3)) if cfg['whorl'] else 1
        for c in range(count):
            if cfg['whorl']:
                ang = math.radians(rng.uniform(55, 85)) * side + (math.radians(rng.uniform(-20, 20)) if c == 2 else 0.0)
            else:
                ang = math.radians(rng.uniform(28, 55)) * side
            side = -side
            fh = hdg + ang
            fr0 = r_at * rng.uniform(0.5, 0.72) * (0.85 if cfg['whorl'] else 1.0)
            flen = L * rng.uniform(0.2, 0.42) * (1.0 - 0.3 * t) * (0.8 if cfg['whorl'] else 1.0)
            roll = rng.random()
            kind = 'lying' if roll < 1 - cfg['raised'] - 0.15 else ('raised' if roll < 0.85 else 'stub')
            fns = 3 if flen > 0.4 else 2
            fsides = 5 if fr0 >= 0.01 else 4
            if kind == 'lying':
                fpath2 = zigzag_path(rng, _v2(base), fh, flen, fns, 10.0)
                frad, _ = path_radii(fpath2, fr0, fr0 * rng.uniform(0.35, 0.55))
                head = fpath2[0] + (fpath2[1] - fpath2[0]) * 0.35
                okf, cr = lay.check([head] + fpath2[1:], [frad[0]] + frad[1:], skip=(0,), cross_r=0.0)
                okf = okf and not any(seg_intersect(fpath2[0] + (fpath2[1] - fpath2[0]) * 0.3, fpath2[-1], q, q2)
                                      for q, q2, za, zb, ra, rb, o in lay.segs if o == 0)
                if not okf:
                    kind = 'raised'
                else:
                    fn = [base.copy()] + [Vector((p.x, p.y, r - sink * 0.5)) for p, r in zip(fpath2[1:], frad[1:])]
                    pieces.append(Piece(fn, frad, fsides, cfg['fork_region'], start='open',
                                        end='tip' if rng.random() < 0.3 else 'broken', end_block='end_old'))
                    lay.add(fn, frad, 1 + k)
                    hosts.append((fn, frad, False))
            if kind == 'raised':
                el = min(math.radians(rng.uniform(15, 42)), math.asin(min(1.0, 0.36 / flen)))
                fn = path3d(rng, base, fh, el, flen, max(fns, 3), droop=rng.uniform(0.35, 0.8), wobble=10.0)
                frad, _ = path_radii([_v2(p) for p in fn], fr0, fr0 * rng.uniform(0.35, 0.55))
                for p, r in zip(fn, frad):
                    p.z = max(p.z, r - sink * 0.5)
                pieces.append(Piece(fn, frad, fsides, cfg['fork_region'], start='open',
                                    end='tip' if rng.random() < 0.35 else 'broken', end_block='end_old'))
                hosts.append((fn, frad, True))
            elif kind == 'stub':
                el = math.radians(rng.uniform(10, 45))
                slen = rng.uniform(0.06, 0.18)
                fn = path3d(rng, base, fh, el, slen, 1, droop=0.0)
                pieces.append(Piece(fn, [fr0, fr0 * 0.9], fsides, cfg['fork_region'], start='open', end='broken',
                                    end_block='end_old'))
    # веточки: на вилках и у вершины главной оси
    lo, hi = cfg['twigs_per_fork']
    n_tw = 0
    for fn, frad, raised in hosts:
        for _ in range(max(0, rng.randint(lo, hi) - trim)):
            add_twig(rng, pieces, fn, frad, raised, cfg['twig_region'])
            n_tw += 1
    cut = int(nseg * 0.55)
    for _ in range(max(0, cfg['tip_twigs'] - trim)):
        add_twig(rng, pieces, nodes[cut:], radii[cut:], False, cfg['twig_region'])
        n_tw += 1
    # пеньки сучков на главной оси
    for s in range(max(0, 3 - trim)):
        i = rng.randrange(1, nseg - 1)
        base = nodes[i]
        hdg = heading_at(path, i) + math.radians(rng.uniform(45, 90)) * rng.choice((-1, 1))
        el = math.radians(rng.uniform(15, 60))
        d = Vector((math.cos(hdg) * math.cos(el), math.sin(hdg) * math.cos(el), math.sin(el)))
        stubs.append((base, d, radii[i] * 0.45, radii[i] * rng.uniform(1.6, 2.6), cfg['region']))
    for p in pieces:
        emit_piece(m, p, rng)
    for base, d, r, length, region in stubs:
        stub_cone(m, base, d, r, length, region, rng)
    return m, dict(forks=n_forks, twigs=n_tw, pieces=len(pieces))


# ------------------------------------------------------------------ экспорт и манифест

def material_entry(prefix):
    return dict(id=prefix, role='bark', albedo=prefix + '_albedo.png', normal=prefix + '_normal.png',
                mask=prefix + '_mask.png', layerMask='layermask_none.png', tiling=[1, 1], barkTiling=[1, 1],
                blendTiling=[1, 1], normalScale=1.0, barkNormalScale=1.0, smoothnessMax=1.0, aoMax=1.0, metallic=0.0,
                stiffness=1.0, drag=0.0001, initialBend=0.0, heightDrag=5.0)


NR = {
    'twigs': dict(renderDistance=35.0, shadows=False, renderShadows=False, densityInDistance=0.3),
    'pebbles': dict(renderDistance=25.0, shadows=False, renderShadows=False, densityInDistance=0.3),
}


def mesh_stats(obj):
    me = obj.data
    me.update()
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    co = np.array([v.co[:] for v in me.vertices], np.float32)
    lo, hi = co.min(0), co.max(0)
    return dict(triangles=int(tris), size=[round(float(x), 4) for x in (hi - lo)], minZ=round(float(lo[2]), 4),
                maxZ=round(float(hi[2]), 4))


def copy_textures(folder, tex_dir, prefixes):
    for p in prefixes:
        for suffix in ('_albedo.png', '_normal.png', '_mask.png'):
            shutil.copyfile(os.path.join(tex_dir, p + suffix), os.path.join(folder, p + suffix))
    shutil.copyfile(os.path.join(tex_dir, 'layermask_none.png'), os.path.join(folder, 'layermask_none.png'))


def write_asset(folder, name, kind, obj, material_prefix, tex_dir, extra=None):
    """kind: 'twigs' | 'pebbles' (детали, один <name>.fbx) или 'prop' (объект точки взлёта: <name>_LOD0.fbx)."""
    os.makedirs(folder, exist_ok=True)
    detail = kind in ('twigs', 'pebbles')
    fbx = name + ('.fbx' if detail else '_LOD0.fbx')
    eg.export_fbx([obj], os.path.join(folder, fbx))
    st = mesh_stats(obj)
    copy_textures(folder, tex_dir, [material_prefix])
    manifest = dict(name=name, type='detail' if detail else 'prop', group=GROUP, height=st['maxZ'],
                    trunkRadius=0.0, colliderHeight=0.0, evergreen=False,
                    lods=[dict(fbx=fbx, screen=0.0 if detail else 0.004, submeshes=[material_prefix],
                               castShadows=not detail, triangles=st['triangles'])],
                    materials=[material_entry(material_prefix)], nr=NR[kind] if detail else None,
                    size=st['size'], triangles=st['triangles'])
    if extra:
        manifest['info'] = extra
    with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=1)
    with open(os.path.join(folder, 'stats.json'), 'w', encoding='utf-8') as f:
        json.dump(dict(st, **(extra or {})), f, indent=1)
    print('GROUND_ASSET', name, json.dumps(st))
    return st


def step_wood(out, assets):
    tex_dir = os.path.join(out, '_tex')
    eg.reset_scene()
    build_wood_texture(tex_dir)
    report = {}
    for name in assets:
        if name not in TWIGS and name not in BRANCHES:
            continue
        twig = name in TWIGS
        budget = TWIGS[name]['budget'] if twig else 900
        for trim in range(6):
            eg.reset_scene()
            m, info = build_twigs(name, trim) if twig else build_branch(name, trim)
            if m.triangles <= budget:
                break
            print('GROUND_TRIM', name, trim, m.triangles)
        obj = m.to_object(name, placeholder_material('ground_wood'))
        if not twig:
            # пивот — середина по X/Y на земле (радиус следа у генератора точки взлёта меньше)
            co = np.array([v.co[:] for v in obj.data.vertices], np.float32)
            cx, cy = (co[:, 0].min() + co[:, 0].max()) * 0.5, (co[:, 1].min() + co[:, 1].max()) * 0.5
            obj.data.transform(Matrix.Translation((-cx, -cy, 0.0)))
            obj.data.update()
        info['trim'] = trim
        report[name] = write_asset(os.path.join(out, name), name, 'twigs' if twig else 'prop', obj, 'ground_wood',
                                   tex_dir, info)
    return report


# ------------------------------------------------------------------ галька и камни: формы

# 16 галек: (вид, длина a, м). Ячейки атласа 0..15 (слева 4×4 по 256²)
PEBBLES = [
    ('flint', 0.026), ('flint', 0.034), ('flint', 0.046), ('flint', 0.060), ('flint', 0.076),
    ('chalk', 0.022), ('chalk', 0.029), ('chalk', 0.037), ('chalk', 0.050), ('chalk', 0.066),
    ('granite', 0.018), ('granite', 0.025), ('granite', 0.034),
    ('limestone', 0.020), ('limestone', 0.028), ('limestone', 0.038),
]
STONES = {
    'ground_stone_01': dict(kind='chalk', size=(0.22, 0.17, 0.12), cell=0),
    'ground_stone_02': dict(kind='limestone', shape='limestone_big', size=(0.26, 0.19, 0.13), cell=1, lichen=True),
    'ground_stone_03': dict(kind='flint', size=(0.17, 0.125, 0.10), cell=2),
    'ground_stone_04': dict(kind='sandstone', size=(0.30, 0.24, 0.09), cell=3, lichen=True),
}
SOIL_FIELD = '#5E4E3E'


def icosphere_bm(target_faces):
    for sub in range(1, 9):
        bm = bmesh.new()
        bmesh.ops.create_icosphere(bm, subdivisions=sub, radius=1.0)
        if len(bm.faces) >= target_faces:
            return bm
        bm.free()
    raise RuntimeError('icosphere')


def stone_params(kind, abc, rng):
    """Параметры формы: полуоси, смещение (fractal + noise), бугры, плоскости сколов, показатель суперэллипсоида."""
    a, b, c = abc
    off = Vector((rng.uniform(-50, 50), rng.uniform(-50, 50), rng.uniform(-50, 50)))
    p = dict(half=(a * 0.5, b * 0.5, c * 0.5), off=off, off2=off * 1.7 + Vector((13, 7, 3)), freq=1.4,
             fa=0.10, na=0.025, lumps=[], cuts=[], sq=2.0, beds=0.0)
    if kind == 'flint':
        p['fa'] = 0.09
        for _ in range(rng.randint(3, 6)):
            d = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.6, 1))).normalized()
            p['lumps'].append((d, rng.uniform(0.12, 0.32) * rng.choice((1, 1, 1, -0.6)), rng.uniform(0.25, 0.5)))
        # кремни полей — большей частью колотые (морозом, плугом): тёмные сколы, корка остатками
        for _ in range(rng.randint(2, 4)):
            n = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.1, 1))).normalized()
            p['cuts'].append((n, rng.uniform(0.3, 0.7)))
    elif kind in ('chalk', 'limestone_big'):
        # комья мела и окатанный известняк: неровные, с бугром-другим и мягкими старыми сколами
        p['fa'], p['na'], p['freq'] = 0.13, 0.035, 1.8
        p['sq'] = 2.3
        for _ in range(rng.randint(2, 4)):
            d = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.5, 1))).normalized()
            p['lumps'].append((d, rng.uniform(0.06, 0.15) * rng.choice((1, 1, -0.7)), rng.uniform(0.3, 0.5)))
        for _ in range(rng.randint(1, 2)):
            n = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.3, 1))).normalized()
            p['cuts'].append((n, rng.uniform(0.62, 0.85)))
    elif kind in ('granite', 'limestone'):
        p['fa'], p['na'] = 0.05, 0.015
        p['sq'] = 2.4
        for _ in range(rng.randint(4, 6)):
            n = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-0.4, 1))).normalized()
            p['cuts'].append((n, rng.uniform(0.5, 0.82)))
    elif kind == 'sandstone':
        p['fa'], p['na'], p['freq'] = 0.06, 0.02, 1.2
        p['sq'] = 3.2
        p['beds'] = 1.0
    return p


def shape_point(d, p):
    hx, hy, hz = p['half']
    n = p['sq']
    if n != 2.0:
        s = (abs(d.x) ** n + abs(d.y) ** n + abs(d.z) ** n) ** (-1.0 / n)
        d = d * s
    q = d * p['freq'] + p['off']
    disp = p['fa'] * mnoise.fractal(q, 1.0, 2.0, 4) + p['na'] * mnoise.noise(q * 4.0 + p['off2'])
    du = d.normalized()
    for ld, amp, w in p['lumps']:
        disp += amp * math.exp(-((1.0 - du.dot(ld)) / (w * w)) * 2.0)
    pt = Vector((d.x * hx, d.y * hy, d.z * hz)) * (1.0 + disp)
    if p['beds']:
        side = 1.0 - abs(du.z)
        pt += Vector((du.x, du.y, 0)).normalized() * (0.0025 * math.sin(pt.z / 0.006 * math.pi)
                                                      + 0.0015 * mnoise.noise(q * 9.0)) * side
    cut = 0.0
    for nrm, frac in p['cuts']:
        support = math.sqrt((hx * nrm.x) ** 2 + (hy * nrm.y) ** 2 + (hz * nrm.z) ** 2)
        dd = pt.dot(nrm) - frac * support
        if dd > 0:
            pt -= nrm * dd
            pt += nrm * (0.012 * support * mnoise.noise(pt / (support * 0.35) + p['off2']))   # волна раковистого скола
            cut = 1.0
    return pt, cut


def make_high(name, kind, abc, rng, target_faces, tilt_deg=0.0):
    """Высокая модель (Z вверх): икосфера со смещением, сколы, поворот (наклон до tilt_deg). Атрибут cutmask —
    1 на сколах (там кремень тёмный, вне — корка). Возвращает объект с центром в начале координат."""
    p = stone_params(kind, abc, rng)
    bm = icosphere_bm(target_faces)
    cut_vals = []
    for v in bm.verts:
        pt, cut = shape_point(v.co.normalized(), p)
        v.co = pt
        cut_vals.append(cut)
    rot = (Matrix.Rotation(math.radians(rng.uniform(-tilt_deg, tilt_deg)), 4, 'X')
           @ Matrix.Rotation(math.radians(rng.uniform(-tilt_deg, tilt_deg)), 4, 'Y'))
    bmesh.ops.transform(bm, matrix=rot, verts=bm.verts)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    attr = me.attributes.new('cutmask', 'FLOAT', 'POINT')
    for i, cv in enumerate(cut_vals):
        attr.data[i].value = cv
    for poly in me.polygons:
        poly.use_smooth = True
    obj = bpy.data.objects.new(name, me)
    eg.link(obj)
    return obj


def make_low(high, name, target_tris, clip_z=None):
    """Низкая: копия высокой, срез ниже clip_z (галька: часть в земле не видна), Decimate до target_tris."""
    me = high.data.copy()
    me.name = name
    if 'cutmask' in me.attributes:
        me.attributes.remove(me.attributes['cutmask'])
    if clip_z is not None:
        bm = bmesh.new()
        bm.from_mesh(me)
        geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
        bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-7, plane_co=(0, 0, clip_z), plane_no=(0, 0, 1),
                               clear_inner=True)
        bm.to_mesh(me)
        bm.free()
    obj = bpy.data.objects.new(name, me)
    eg.link(obj)
    faces = len(me.polygons)
    ratio = min(1.0, target_tris / max(faces, 1))
    for attempt in range(8):
        mod = obj.modifiers.new('dec', 'DECIMATE')
        mod.decimate_type = 'COLLAPSE'
        mod.ratio = ratio
        mod.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        dg = bpy.context.evaluated_depsgraph_get()
        tris = sum(len(pp.vertices) - 2 for pp in obj.evaluated_get(dg).data.polygons)
        if tris <= target_tris * 1.04 or attempt == 7:
            bpy.ops.object.modifier_apply(modifier=mod.name)
            break
        obj.modifiers.remove(mod)
        ratio *= target_tris / tris * 0.98
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(obj.data)
    bm.free()
    for poly in obj.data.polygons:
        poly.use_smooth = True
    return obj


def smart_uv(obj, margin=0.03, pad=0.025):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    if not obj.data.uv_layers:
        obj.data.uv_layers.new(name='UVMap')
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=margin, area_weight=0.0,
                             correct_aspect=True, scale_to_bounds=True)
    bpy.ops.object.mode_set(mode='OBJECT')
    # отступ от края ячейки (мипы не смешивают соседние ячейки атласа)
    for d in obj.data.uv_layers.active.data:
        d.uv = (pad + d.uv[0] * (1 - 2 * pad), pad + d.uv[1] * (1 - 2 * pad))


# ------------------------------------------------------------------ материалы высокой модели (процедурные, для запечки)

class NB:
    def __init__(self, mat):
        mat.use_nodes = True
        self.nt = mat.node_tree
        self.nt.nodes.clear()

    def new(self, kind, **props):
        n = self.nt.nodes.new(kind)
        for k, v in props.items():
            setattr(n, k, v)
        return n

    @staticmethod
    def sock(node, name, outputs=False):
        coll = node.outputs if outputs else node.inputs
        for s in coll:
            if s.name == name and s.enabled:
                return s
        return coll[name]

    def link(self, src, dst):
        self.nt.links.new(src, dst)

    def set(self, node, name, value):
        s = self.sock(node, name)
        if isinstance(value, bpy.types.NodeSocket):
            self.link(value, s)
        else:
            s.default_value = value

    def coords(self, offset):
        tc = self.new('ShaderNodeTexCoord')
        add = self.new('ShaderNodeVectorMath', operation='ADD')
        self.link(tc.outputs['Object'], add.inputs[0])
        add.inputs[1].default_value = offset
        return add.outputs['Vector']

    def noise(self, vec, scale, detail=4.0, rough=0.55, distortion=0.0):
        n = self.new('ShaderNodeTexNoise')
        self.link(vec, n.inputs['Vector'])
        n.inputs['Scale'].default_value = scale
        n.inputs['Detail'].default_value = detail
        n.inputs['Roughness'].default_value = rough
        n.inputs['Distortion'].default_value = distortion
        return n.outputs['Fac']

    def voronoi(self, vec, scale, feature='F1', out='Distance'):
        n = self.new('ShaderNodeTexVoronoi', feature=feature)
        self.link(vec, n.inputs['Vector'])
        n.inputs['Scale'].default_value = scale
        return self.sock(n, out, outputs=True)

    def ramp(self, fac, stops, interp='LINEAR'):
        r = self.new('ShaderNodeValToRGB')
        r.color_ramp.interpolation = interp
        els = r.color_ramp.elements
        while len(els) < len(stops):
            els.new(0.5)
        for e, (pos, col) in zip(els, stops):
            e.position = pos
            if isinstance(col, str):
                e.color = (*eg.hex_rgb(col), 1.0)
            else:
                e.color = (col, col, col, 1.0)
        self.link(fac, r.inputs['Fac'])
        return r.outputs['Color']

    def mix(self, fac, a, b, blend='MIX'):
        n = self.new('ShaderNodeMix', data_type='RGBA', blend_type=blend)
        self.set(n, 'Factor', fac)
        self.set(n, 'A', a)
        self.set(n, 'B', b)
        return self.sock(n, 'Result', outputs=True)

    def mixf(self, fac, a, b):
        n = self.new('ShaderNodeMix', data_type='FLOAT')
        self.set(n, 'Factor', fac)
        self.set(n, 'A', a)
        self.set(n, 'B', b)
        return self.sock(n, 'Result', outputs=True)

    def math(self, op, a, b=0.0, clamp=False):
        n = self.new('ShaderNodeMath', operation=op, use_clamp=clamp)
        self.set(n, 'Value', a)
        if isinstance(b, bpy.types.NodeSocket):
            self.link(b, n.inputs[1])
        else:
            n.inputs[1].default_value = b
        return n.outputs['Value']

    def ss(self, e0, e1, x):
        n = self.new('ShaderNodeMapRange', interpolation_type='SMOOTHSTEP')
        self.link(x, n.inputs['Value'])
        n.inputs['From Min'].default_value = e0
        n.inputs['From Max'].default_value = e1
        return n.outputs['Result']

    def attr(self, name):
        n = self.new('ShaderNodeAttribute', attribute_type='GEOMETRY', attribute_name=name)
        return n.outputs['Fac']

    def z(self, vec):
        n = self.new('ShaderNodeSeparateXYZ')
        self.link(vec, n.inputs['Vector'])
        return n.outputs['Z']

    def normal_z(self):
        g = self.new('ShaderNodeNewGeometry')
        vt = self.new('ShaderNodeVectorTransform', vector_type='NORMAL', convert_from='WORLD', convert_to='OBJECT')
        self.link(g.outputs['Normal'], vt.inputs['Vector'])
        return self.z(vt.outputs['Vector'])


def stone_material(name, kind, size, ground_z, top_z, rng, lichen=False):
    """Процедурный материал камня в координатах объекта (метры): выходы цвета, гладкости и высоты (рельеф).
    Узел вывода переключается для проходов: EMIT цвет / EMIT гладкость / Principled с рельефом (нормали)."""
    mat = bpy.data.materials.new(name)
    nb = NB(mat)
    co = nb.coords((rng.uniform(-30, 30), rng.uniform(-30, 30), rng.uniform(-30, 30)))
    s = 1.0 / size
    fine = nb.noise(co, 120 * s ** 0.5 * 4, 6, 0.6)
    if kind == 'flint':
        cut = nb.ss(0.3, 0.9, nb.attr('cutmask'))
        wear = nb.ss(0.55, 0.7, nb.noise(co, 3.5 * s, 3, 0.5))           # стёртая корка — серо-бурая
        cortex = nb.ramp(nb.noise(co, 30 * s, 5, 0.6), [(0.3, '#B6AC98'), (0.7, '#D4CCBC')])
        cortex = nb.mix(nb.ss(0.6, 0.75, nb.noise(co, 9 * s, 4)), cortex, nb.ramp(fine, [(0.3, '#A89478'), (0.7, '#BCA88A')]))
        cortex = nb.mix(nb.math('MULTIPLY', wear, 0.55), cortex, (*eg.hex_rgb('#8E8270'), 1))
        body = nb.ramp(nb.noise(co, 7 * s, 5, 0.55), [(0.25, '#36312D'), (0.55, '#4A4440'), (0.8, '#5A534C')])
        body = nb.mix(nb.ss(0.6, 0.75, nb.noise(co, 2.5 * s, 3)), body, (*eg.hex_rgb('#4A4A4C'), 1))
        patina = nb.ss(0.62, 0.78, nb.noise(co, 5 * s, 4))
        body = nb.mix(nb.math('MULTIPLY', patina, 0.45), body, (*eg.hex_rgb('#8C877F'), 1))
        rim = nb.math('MULTIPLY', nb.ss(0.25, 0.55, nb.attr('cutmask')), nb.ss(0.95, 0.7, nb.attr('cutmask')))
        body = nb.mix(nb.math('MULTIPLY', rim, 0.6), body, (*eg.hex_rgb('#7A7064'), 1))
        col = nb.mix(cut, cortex, body)
        smooth = nb.mixf(cut, 0.08, 0.5)
        rings = nb.new('ShaderNodeTexWave', wave_type='RINGS')
        nb.link(co, rings.inputs['Vector'])
        rings.inputs['Scale'].default_value = 2.5 * s
        rings.inputs['Distortion'].default_value = 4.0
        rings.inputs['Detail'].default_value = 2.0
        conch = nb.math('ADD', nb.math('MULTIPLY', rings.outputs['Fac'], 0.25), nb.math('MULTIPLY', fine, 0.06))
        height = nb.mixf(cut, nb.math('MULTIPLY', fine, 0.9), conch)
        soil_amt = 0.45
    elif kind == 'chalk':
        col = nb.ramp(nb.noise(co, 6 * s, 4, 0.55), [(0.3, '#ADA594'), (0.7, '#CFC9BC')])
        stain = nb.ss(0.55, 0.75, nb.noise(co, 3 * s, 3))
        col = nb.mix(nb.math('MULTIPLY', stain, 0.65), col, (*eg.hex_rgb('#A8997E'), 1))
        pits = nb.ss(0.08, 0.02, nb.voronoi(co, 90 * s ** 0.5 * 3))
        col = nb.mix(nb.math('MULTIPLY', pits, 0.7), col, (*eg.hex_rgb('#8F8676'), 1))
        grit = nb.ss(0.04, 0.0, nb.voronoi(co, 140 * s ** 0.5 * 3))
        col = nb.mix(nb.math('MULTIPLY', grit, 0.8), col, (*eg.hex_rgb('#3E3A36'), 1))
        algae = nb.ss(0.6, 0.78, nb.noise(co, 4 * s, 4))
        col = nb.mix(nb.math('MULTIPLY', algae, 0.25), col, (*eg.hex_rgb('#8C9070'), 1))
        smooth = nb.math('ADD', nb.math('MULTIPLY', fine, 0.04), 0.05)
        height = nb.math('SUBTRACT', nb.math('MULTIPLY', fine, 0.7), nb.math('MULTIPLY', pits, 0.6))
        soil_amt = 0.85
    elif kind == 'granite':
        cell = nb.voronoi(co, 520.0, 'F1', 'Color')
        sep = nb.new('ShaderNodeSeparateColor')
        nb.link(cell, sep.inputs['Color'])
        col = nb.ramp(sep.outputs['Red'], [(0.0, '#B0958A'), (0.36, '#9B9893'), (0.64, '#CBC7C0'), (0.8, '#2D2B2A')],
                      'CONSTANT')
        col = nb.mix(0.28, col, (*eg.hex_rgb('#8E8580'), 1))
        col = nb.mix(nb.math('MULTIPLY', fine, 0.3), col, (*eg.hex_rgb('#6E6660'), 1))
        smooth = nb.math('ADD', nb.math('MULTIPLY', nb.ss(0.75, 0.8, sep.outputs['Red']), 0.15), 0.22)
        height = nb.math('MULTIPLY', nb.voronoi(co, 520.0, 'F1', 'Distance'), 0.6)
        soil_amt = 0.35
    elif kind == 'limestone':
        col = nb.ramp(nb.noise(co, 8 * s, 5, 0.6), [(0.3, '#9C9586'), (0.7, '#BAB3A4')])
        streak = nb.ss(0.6, 0.72, nb.noise(co, 2.2 * s, 3, 0.5, 2.0))
        col = nb.mix(nb.math('MULTIPLY', streak, 0.35), col, (*eg.hex_rgb('#8E877B'), 1))
        frag = nb.ss(0.06, 0.02, nb.voronoi(co, 60 * s ** 0.5 * 3))
        col = nb.mix(nb.math('MULTIPLY', frag, 0.5), col, (*eg.hex_rgb('#7F7769'), 1))
        smooth = nb.math('ADD', nb.math('MULTIPLY', fine, 0.05), 0.1)
        height = nb.math('SUBTRACT', nb.math('MULTIPLY', fine, 0.6), nb.math('MULTIPLY', frag, 0.3))
        soil_amt = 0.5
    else:   # sandstone
        bands = nb.new('ShaderNodeTexWave', wave_type='BANDS', bands_direction='Z')
        nb.link(co, bands.inputs['Vector'])
        bands.inputs['Scale'].default_value = 18.0
        bands.inputs['Distortion'].default_value = 3.0
        bands.inputs['Detail'].default_value = 2.0
        mott = nb.ramp(nb.noise(co, 5 * s, 5, 0.6), [(0.3, '#9C8B6F'), (0.7, '#B2A184')])
        col = nb.mix(nb.math('MULTIPLY', bands.outputs['Fac'], 0.18), mott, (*eg.hex_rgb('#8E7D62'), 1))
        iron = nb.ss(0.6, 0.75, nb.noise(co, 4 * s, 4))
        col = nb.mix(nb.math('MULTIPLY', iron, 0.45), col, (*eg.hex_rgb('#A9814F'), 1))
        grain = nb.ss(0.05, 0.0, nb.voronoi(co, 900.0))
        col = nb.mix(nb.math('MULTIPLY', grain, 0.35), col, (*eg.hex_rgb('#6E604C'), 1))
        col = nb.mix(nb.math('MULTIPLY', fine, 0.25), col, (*eg.hex_rgb('#8E7D62'), 1))
        smooth = nb.math('ADD', nb.math('MULTIPLY', fine, 0.03), 0.07)
        height = nb.math('ADD', nb.math('MULTIPLY', fine, 0.5), nb.math('MULTIPLY', bands.outputs['Fac'], 0.3))
        soil_amt = 0.5
    if lichen:
        top = nb.ss(0.15, 0.6, nb.normal_z())
        lich = nb.math('MULTIPLY', nb.ss(0.58, 0.63, nb.noise(co, 9 * s, 5, 0.65)), top)
        lich_c = nb.ramp(nb.noise(co, 60 * s, 3), [(0.35, '#8E9478'), (0.7, '#A9AE9A')])
        col = nb.mix(nb.math('MULTIPLY', lich, 0.9), col, lich_c)
        orange = nb.math('MULTIPLY', nb.ss(0.68, 0.71, nb.noise(co, 14 * s, 4, 0.6)), top)
        col = nb.mix(nb.math('MULTIPLY', orange, 0.8), col, (*eg.hex_rgb('#B98A45'), 1))
        dots = nb.ss(0.035, 0.01, nb.voronoi(co, 25 * s))
        col = nb.mix(nb.math('MULTIPLY', dots, 0.7), col, (*eg.hex_rgb('#2E2C28'), 1))
        smooth = nb.mixf(lich, smooth, 0.05)
        height = nb.math('ADD', height, nb.math('MULTIPLY', lich, 0.5))
    # земля на нижней части у линии заглубления (камень «сидит» в почве)
    zz = nb.z(co)
    zn = nb.math('ADD', zz, nb.math('MULTIPLY', nb.math('SUBTRACT', nb.noise(co, 8 * s, 3), 0.5), (top_z - ground_z) * 0.35))
    soil = nb.ss(ground_z + (top_z - ground_z) * 0.4, ground_z - (top_z - ground_z) * 0.05, zn)
    soil_c = nb.ramp(nb.noise(co, 20 * s, 3), [(0.3, '#4F4134'), (0.7, SOIL_FIELD)])
    col = nb.mix(nb.math('MULTIPLY', soil, soil_amt), col, soil_c)
    smooth = nb.mixf(nb.math('MULTIPLY', soil, soil_amt), smooth, 0.04)
    # выводы
    out = nb.new('ShaderNodeOutputMaterial')
    em_col = nb.new('ShaderNodeEmission')
    nb.link(col, em_col.inputs['Color'])
    em_sm = nb.new('ShaderNodeEmission')
    nb.link(smooth, em_sm.inputs['Color'])
    em_white = nb.new('ShaderNodeEmission')
    em_white.inputs['Color'].default_value = (1, 1, 1, 1)
    bump = nb.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = 0.6
    bump.inputs['Distance'].default_value = 0.0008 * (size / 0.05) ** 0.5
    nb.link(height, bump.inputs['Height'])
    bsdf = nb.new('ShaderNodeBsdfPrincipled')
    nb.link(bump.outputs['Normal'], bsdf.inputs['Normal'])
    return mat, dict(out=out, color=em_col, smooth=em_sm, bsdf=bsdf, white=em_white)


def set_pass(mat_info, which):
    out = mat_info['out']
    nt = out.id_data
    for l in list(out.inputs['Surface'].links):
        nt.links.remove(l)
    src = {'color': mat_info['color'].outputs[0], 'smooth': mat_info['smooth'].outputs[0],
           'normal': mat_info['bsdf'].outputs[0], 'cov': mat_info['white'].outputs[0]}[which]
    nt.links.new(src, out.inputs['Surface'])


# ------------------------------------------------------------------ запечка

def bake_cell(scene, high, low, minfo, cell_px, size, ground_z=None):
    """Запечка high -> low в картинки cell_px²: цвет, гладкость, нормаль (касательная, OpenGL), AO (земля на ground_z).
    Возвращает словарь numpy-массивов (строки сверху вниз) и маску покрытия."""
    for o in scene.objects:
        o.hide_render = o not in (high, low)
    plane = None
    if ground_z is not None:
        bpy.ops.mesh.primitive_plane_add(size=max(1.0, size * 20), location=(0, 0, ground_z))
        plane = bpy.context.active_object
        plane.hide_render = False
    low.visible_diffuse = low.visible_glossy = low.visible_shadow = low.visible_transmission = False
    low.visible_volume_scatter = False
    bake_mat = bpy.data.materials.new(low.name + '_bake')
    bake_mat.use_nodes = True
    tex = bake_mat.node_tree.nodes.new('ShaderNodeTexImage')
    bake_mat.node_tree.nodes.active = tex
    low.data.materials.clear()
    low.data.materials.append(bake_mat)
    high.data.materials.clear()
    high.data.materials.append(minfo['mat'])
    ext = 0.25 * size
    results = {}
    scene.world.light_settings.distance = 0.4 * size
    for which, btype, samples in (('cov', 'EMIT', 1), ('color', 'EMIT', 4), ('smooth', 'EMIT', 4), ('normal', 'NORMAL', 4),
                                  ('ao', 'AO', 32)):
        img = bpy.data.images.new(f'{low.name}_{which}', cell_px, cell_px, alpha=True, float_buffer=True)
        img.colorspace_settings.name = 'Non-Color' if which != 'color' else 'Linear Rec.709'
        img.pixels[:] = np.zeros(cell_px * cell_px * 4, np.float32)
        tex.image = img
        if which != 'ao':
            set_pass(minfo, which)
        scene.cycles.samples = samples
        bpy.ops.object.select_all(action='DESELECT')
        high.select_set(True)
        low.select_set(True)
        bpy.context.view_layer.objects.active = low
        kw = dict(type=btype, use_selected_to_active=True, cage_extrusion=ext, max_ray_distance=ext * 2.0,
                  margin=8, margin_type='EXTEND', use_clear=True, target='IMAGE_TEXTURES')
        if btype == 'NORMAL':
            kw.update(normal_space='TANGENT', normal_r='POS_X', normal_g='POS_Y', normal_b='POS_Z')
        bpy.ops.object.bake(**kw)
        px = np.array(img.pixels[:], np.float32).reshape(cell_px, cell_px, 4)[::-1].copy()
        results[which] = px
        bpy.data.images.remove(img)
    if plane is not None:
        bpy.data.objects.remove(plane)
    for o in scene.objects:
        o.hide_render = False
    return results


def setup_bake_scene():
    scene = eg.reset_scene()
    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.use_denoising = False
    world = bpy.data.worlds.new('w')
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Color'].default_value = (1, 1, 1, 1)
    scene.world = world
    return scene


def fill_cell(res):
    """Покрытие — проход белой эмиссии (вместе с полями запечки), остальное растекается (мипы не тянут чёрное)."""
    cov = res['cov'][..., 0] > 0.5
    cov &= res['color'][..., :3].sum(-1) > 1e-4       # луч не нашёл высокую модель — пусть заполнит растекание
    out = {}
    for k, px in res.items():
        if k == 'cov':
            continue
        a = px.copy()
        a[..., 3] = cov.astype(np.float32)
        out[k] = eg.dilate_rgb(a, 96)[..., :3]
    return out, cov


def step_stone(out, assets):
    tex_dir = os.path.join(out, '_tex')
    os.makedirs(tex_dir, exist_ok=True)
    scene = setup_bake_scene()
    W, H = 2048, 1024
    atlas = {k: np.zeros((H, W, 3), np.float32) for k in ('color', 'smooth', 'normal', 'ao')}
    atlas['normal'][:] = (0.5, 0.5, 1.0)
    lows = {}
    meta = {}
    # гальки: 256², ячейка k -> столбец k % 4, ряд k // 4 (слева)
    for k, (kind, a) in enumerate(PEBBLES):
        rng = random.Random(zlib.crc32(f'ground_pebble_{k:02d}'.encode()))
        gravel = kind in ('granite', 'limestone')
        ratio = (0.72, 0.55) if gravel else (0.75, 0.45)
        abc = (a, a * ratio[0] * rng.uniform(0.88, 1.12), a * ratio[1] * rng.uniform(0.85, 1.15))
        high = make_high(f'peb{k:02d}_high', kind, abc, rng, 5000, tilt_deg=15.0)
        co = np.array([v.co[:] for v in high.data.vertices], np.float32)
        zmin, zmax = float(co[:, 2].min()), float(co[:, 2].max())
        h = zmax - zmin
        sink = rng.uniform(0.30, 0.45)
        shift = -(zmin + sink * h)
        high.data.transform(Matrix.Translation((0, 0, shift)))
        zmin += shift
        zmax += shift
        buried = -zmin
        skirt = min(buried, max(0.4 * buried, 0.003))
        if a < 0.03:
            target = 24
        elif a < 0.045:
            target = 42
        else:
            target = 72
        if gravel:
            target = 20 if a < 0.025 else 30
        low = make_low(high, f'peb{k:02d}_low', target, clip_z=-skirt)
        smart_uv(low, 0.04, 6.0 / 256)
        mat, info = stone_material(f'peb{k:02d}_mat', kind, a, 0.0, zmax, rng)
        info['mat'] = mat
        res = bake_cell(scene, high, low, info, 256, a, ground_z=0.0)
        filled, cov = fill_cell(res)
        col_i, row_i = k % 4, k // 4
        y0, x0 = row_i * 256, col_i * 256
        for key in atlas:
            atlas[key][y0:y0 + 256, x0:x0 + 256] = filled[key]
        # UV -> ячейка атласа
        uvl = low.data.uv_layers['UVMap'].data
        for d in uvl:
            u, v = d.uv
            d.uv = ((x0 + u * 256) / W, 1.0 - (y0 + (1 - v) * 256) / H)
        bpy.data.objects.remove(high)
        lows[k] = low
        meta[k] = dict(kind=kind, a=a, sink=round(sink, 3), triangles=len(low.data.polygons), height=round(h, 4),
                       top=round(zmax, 4))
        print('GROUND_PEBBLE', k, json.dumps(meta[k]))
    # камни: 512², справа 2×2 (запекаются все четыре — ячейки атласа общие для группы)
    stone_objs = {}
    for name, cfg in STONES.items():
        rng = random.Random(zlib.crc32(name.encode()))
        high = make_high(name + '_high', cfg.get('shape', cfg['kind']), cfg['size'], rng, 20000, tilt_deg=0.0)
        co = np.array([v.co[:] for v in high.data.vertices], np.float32)
        high.data.transform(Matrix.Translation((-(co[:, 0].min() + co[:, 0].max()) * 0.5,
                                                -(co[:, 1].min() + co[:, 1].max()) * 0.5, -co[:, 2].min())))
        h = float(co[:, 2].max() - co[:, 2].min())
        low = make_low(high, name + '_low', 316)
        smart_uv(low, 0.02, 8.0 / 512)
        size = max(cfg['size'])
        mat, info = stone_material(name + '_mat', cfg['kind'], size, 0.25 * h, h, rng, lichen=cfg.get('lichen', False))
        info['mat'] = mat
        res = bake_cell(scene, high, low, info, 512, size, ground_z=0.25 * h)
        filled, cov = fill_cell(res)
        c = cfg['cell']
        y0, x0 = (c // 2) * 512, 1024 + (c % 2) * 512
        for key in atlas:
            atlas[key][y0:y0 + 512, x0:x0 + 512] = filled[key]
        uvl = low.data.uv_layers['UVMap'].data
        for d in uvl:
            u, v = d.uv
            d.uv = ((x0 + u * 512) / W, 1.0 - (y0 + (1 - v) * 512) / H)
        bpy.data.objects.remove(high)
        stone_objs[name] = low
        print('GROUND_STONE', name, len(low.data.polygons), 'h', round(h, 3))
    # атлас: альбедо с лёгкой полостной тенью, маска (R 0, G AO, B 0.5, A гладкость)
    ao = np.clip(atlas['ao'][..., 0], 0, 1)
    albedo = atlas['color'] * (0.8 + 0.2 * ao)[..., None]
    mask = np.zeros((H, W, 4), np.float32)
    mask[..., 1] = ao
    mask[..., 2] = 0.5
    mask[..., 3] = np.clip(atlas['smooth'][..., 0], 0, 1)
    eg.save_png(albedo, os.path.join(tex_dir, 'ground_stone_albedo.png'))
    eg.save_png(np.clip(atlas['normal'], 0, 1), os.path.join(tex_dir, 'ground_stone_normal.png'), linear=True)
    eg.save_png(mask, os.path.join(tex_dir, 'ground_stone_mask.png'), linear=True)
    if not os.path.exists(os.path.join(tex_dir, 'layermask_none.png')):
        eg.save_png(np.zeros((4, 4, 4), np.float32), os.path.join(tex_dir, 'layermask_none.png'), linear=True)
    # готовые объекты: материал-заглушка, цвет вершин, UV2
    stone_mat = placeholder_material('ground_stone')
    for o in list(lows.values()) + list(stone_objs.values()):
        o.data.materials.clear()
        o.data.materials.append(stone_mat)
    report = {}
    for name in PEBBLE_ASSETS:
        if name in assets:
            obj, info = build_pebble_patch(name, lows, meta)
            finish_static(obj)
            report[name] = write_asset(os.path.join(out, name), name, 'pebbles', obj, 'ground_stone', tex_dir, info)
    for name in STONE_ASSETS:
        if name in assets:
            obj = stone_objs[name]
            obj.name = name
            finish_static(obj)
            report[name] = write_asset(os.path.join(out, name), name, 'prop', obj, 'ground_stone', tex_dir,
                                       dict(kind=STONES[name]['kind']))
    with open(os.path.join(tex_dir, 'pebbles.json'), 'w', encoding='utf-8') as f:
        json.dump(meta, f, indent=1)
    return report


def finish_static(obj):
    """Цвет вершин (1, 1, 1, 0) на углах, второй UV-слой (0, z/H), гладкие нормали."""
    me = obj.data
    for name in [a.name for a in me.color_attributes]:
        me.color_attributes.remove(me.color_attributes[name])
    col = me.color_attributes.new(name='Col', type='BYTE_COLOR', domain='CORNER')
    for d in col.data:
        d.color = (1.0, 1.0, 1.0, 0.0)
    while len(me.uv_layers) > 1:
        me.uv_layers.remove(me.uv_layers[-1])
    me.uv_layers[0].name = 'UVMap'
    uv2 = me.uv_layers.new(name='UV2')
    zmax = max((v.co.z for v in me.vertices), default=1.0) or 1.0
    for poly in me.polygons:
        poly.use_smooth = True
        for li in poly.loop_indices:
            uv2.data[li].uv = (0.0, max(0.0, me.vertices[me.loops[li].vertex_index].co.z) / zmax)
    me.update()


PEBBLE_PATCHES = {
    # кремень и мел, 9–14 штук, 60 % мелких (2–4 см) и 40 % крупных (4–8 см)
    'ground_pebbles_01': dict(cells_small=[0, 1, 5, 6, 7], cells_large=[2, 3, 4, 8, 9], count=(9, 14), small=0.6,
                              scale=(0.88, 1.15), radius=0.27, budget=900),
    # щебень гранит/известняк у колеи
    'ground_pebbles_02': dict(cells_small=[10, 11, 13, 14], cells_large=[12, 15], count=(20, 28), small=0.6,
                              scale=(0.75, 1.3), radius=0.28, budget=900),
}


def build_pebble_patch(name, lows, meta):
    cfg = PEBBLE_PATCHES[name]
    rng = random.Random(zlib.crc32(name.encode()))
    n = rng.randint(*cfg['count'])
    centres = [Vector((rng.uniform(-0.12, 0.12), rng.uniform(-0.12, 0.12))) for _ in range(rng.randint(2, 3))]
    placed = []   # (pos2, half_size, cell)
    bags = {'s': [], 'l': []}

    def take(kind):
        if not bags[kind]:
            bags[kind] = list(cfg['cells_small' if kind == 's' else 'cells_large'])
            rng.shuffle(bags[kind])
        return bags[kind].pop()

    tris = 0
    for i in range(n):
        cell = take('s' if rng.random() < cfg['small'] else 'l')
        for attempt in range(300):
            sc = rng.uniform(*cfg['scale'])
            half = meta[cell]['a'] * sc * 0.5
            if rng.random() < 0.6:
                c = rng.choice(centres)
                pos = c + Vector((rng.gauss(0, 0.09), rng.gauss(0, 0.09)))
            else:
                rr = cfg['radius'] * math.sqrt(rng.random())
                aa = rng.uniform(0, math.tau)
                pos = Vector((rr * math.cos(aa), rr * math.sin(aa)))
            if pos.length + half > cfg['radius'] + 0.02:
                continue
            if any((pos - q).length < (half + hq) * 0.95 + 0.004 for q, hq, _, _ in placed):
                continue
            if tris + meta[cell]['triangles'] > cfg['budget']:
                break
            placed.append((pos, half, cell, sc))
            tris += meta[cell]['triangles']
            break
    objs = []
    for pos, half, cell, sc in placed:
        o = lows[cell].copy()
        o.data = lows[cell].data.copy()
        eg.link(o)
        o.matrix_world = (Matrix.Translation((pos.x, pos.y, 0.0)) @ Matrix.Rotation(rng.uniform(0, math.tau), 4, 'Z')
                          @ Matrix.Scale(sc, 4))
        objs.append(o)
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    obj.name = name
    obj.data.name = name
    return obj, dict(pebbles=len(placed), cells=sorted({c for _, _, c, _ in placed}))


# ------------------------------------------------------------------ предпросмотр

PREVIEW = {
    'ground_twigs_01': dict(ground='#4B4030', extent=1.45, scatter=1.6),
    'ground_twigs_02': dict(ground='#4B4030', extent=1.45, scatter=1.6),
    'ground_twigs_03': dict(ground='#6E5440', extent=1.45, scatter=1.6),
    'ground_pebbles_01': dict(ground='#5A4B3C', extent=0.7, scatter=0.9),
    'ground_pebbles_02': dict(ground='#7A6E5E', extent=0.7, scatter=0.9),
    'ground_branch_01': dict(ground='#4B4030', extent=3.0),
    'ground_branch_02': dict(ground='#6E5440', extent=3.2),
    'ground_branch_03': dict(ground='#4B4030', extent=3.4),
    'ground_stone_01': dict(ground='#6A5E4C', extent=0.42, sink=0.35),
    'ground_stone_02': dict(ground='#6A5E4C', extent=0.45, sink=0.35),
    'ground_stone_03': dict(ground='#5A4B3C', extent=0.36, sink=0.35),
    'ground_stone_04': dict(ground='#6A5E4C', extent=0.48, sink=0.40),
}


def preview_material(folder, prefix):
    m = bpy.data.materials.new('prev_' + prefix)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes['Principled BSDF']
    alb = nt.nodes.new('ShaderNodeTexImage')
    alb.image = bpy.data.images.load(os.path.join(folder, prefix + '_albedo.png'))
    nrm = nt.nodes.new('ShaderNodeTexImage')
    nrm.image = bpy.data.images.load(os.path.join(folder, prefix + '_normal.png'))
    nrm.image.colorspace_settings.name = 'Non-Color'
    msk = nt.nodes.new('ShaderNodeTexImage')
    msk.image = bpy.data.images.load(os.path.join(folder, prefix + '_mask.png'))
    msk.image.colorspace_settings.name = 'Non-Color'
    msk.image.alpha_mode = 'CHANNEL_PACKED'
    sep = nt.nodes.new('ShaderNodeSeparateColor')
    nt.links.new(msk.outputs['Color'], sep.inputs['Color'])
    aomix = nt.nodes.new('ShaderNodeMix')
    aomix.data_type = 'RGBA'
    aomix.blend_type = 'MULTIPLY'
    NB.sock(aomix, 'Factor').default_value = 0.5
    nt.links.new(alb.outputs['Color'], NB.sock(aomix, 'A'))
    nt.links.new(sep.outputs['Green'], NB.sock(aomix, 'B'))
    nt.links.new(NB.sock(aomix, 'Result', outputs=True), bsdf.inputs['Base Color'])
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


def soil_material(hex_col):
    m = bpy.data.materials.new('soil')
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes['Principled BSDF']
    nb = NB.__new__(NB)
    nb.nt = nt
    base = Vector(eg.hex_rgb(hex_col))
    tc = nt.nodes.new('ShaderNodeTexCoord')
    n1 = nt.nodes.new('ShaderNodeTexNoise')
    nt.links.new(tc.outputs['Object'], n1.inputs['Vector'])
    n1.inputs['Scale'].default_value = 1.5
    n1.inputs['Detail'].default_value = 6
    r1 = nt.nodes.new('ShaderNodeValToRGB')
    r1.color_ramp.elements[0].position = 0.3
    r1.color_ramp.elements[0].color = (*(base * 0.75), 1)
    r1.color_ramp.elements[1].position = 0.7
    r1.color_ramp.elements[1].color = (*(base * 1.2), 1)
    nt.links.new(n1.outputs['Fac'], r1.inputs['Fac'])
    v = nt.nodes.new('ShaderNodeTexVoronoi')
    nt.links.new(tc.outputs['Object'], v.inputs['Vector'])
    v.inputs['Scale'].default_value = 40.0
    r2 = nt.nodes.new('ShaderNodeValToRGB')
    r2.color_ramp.elements[0].color = (0.8, 0.8, 0.8, 1)
    r2.color_ramp.elements[1].color = (1.15, 1.1, 1.0, 1)
    sepc = nt.nodes.new('ShaderNodeSeparateColor')
    nt.links.new(v.outputs['Color'], sepc.inputs['Color'])
    nt.links.new(sepc.outputs['Red'], r2.inputs['Fac'])
    mul = nt.nodes.new('ShaderNodeMix')
    mul.data_type = 'RGBA'
    mul.blend_type = 'MULTIPLY'
    NB.sock(mul, 'Factor').default_value = 0.6
    nt.links.new(r1.outputs['Color'], NB.sock(mul, 'A'))
    nt.links.new(r2.outputs['Color'], NB.sock(mul, 'B'))
    nt.links.new(NB.sock(mul, 'Result', outputs=True), bsdf.inputs['Base Color'])
    n2 = nt.nodes.new('ShaderNodeTexNoise')
    nt.links.new(tc.outputs['Object'], n2.inputs['Vector'])
    n2.inputs['Scale'].default_value = 90.0
    n2.inputs['Detail'].default_value = 4
    bump = nt.nodes.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = 0.35
    bump.inputs['Distance'].default_value = 0.004
    nt.links.new(n2.outputs['Fac'], bump.inputs['Height'])
    nt.links.new(bump.outputs['Normal'], bsdf.inputs['Normal'])
    bsdf.inputs['Roughness'].default_value = 0.95
    return m


def scale_bar(x, y, yaw=0.0):
    """Линейка 10 см: пять полос по 2 см, чёрная/белая, 1 см шириной, 3 мм высотой."""
    mats = []
    for c in ((0.02, 0.02, 0.02), (0.85, 0.85, 0.85)):
        mm = bpy.data.materials.new('bar')
        mm.use_nodes = True
        mm.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (*c, 1)
        mats.append(mm)
    objs = []
    for i in range(5):
        bpy.ops.mesh.primitive_cube_add(size=1.0)
        o = bpy.context.active_object
        o.scale = (0.02, 0.01, 0.003)
        o.location = (-0.04 + 0.02 * i, 0, 0.0015)
        o.data.materials.append(mats[i % 2])
        objs.append(o)
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    bar = bpy.context.active_object
    bar.location = (x, y, 0)
    bar.rotation_euler = (0, 0, yaw)
    return bar


def import_asset(folder, name):
    fbx = os.path.join(folder, name + '.fbx')
    if not os.path.exists(fbx):
        fbx = os.path.join(folder, name + '_LOD0.fbx')
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=fbx)
    objs = [o for o in bpy.data.objects if o not in before and o.type == 'MESH']
    obj = objs[0]
    with open(os.path.join(folder, 'manifest.json'), encoding='utf-8') as f:
        prefix = json.load(f)['materials'][0]['id']
    obj.data.materials.clear()
    obj.data.materials.append(preview_material(folder, prefix))
    return obj


def look_at(cam, target):
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat('-Z', 'Y').to_euler()


def render(scene, path, samples=64):
    scene.cycles.samples = samples
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def setup_preview_scene(ground_hex):
    scene = eg.reset_scene()
    scene.render.engine = 'CYCLES'
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        prefs.compute_device_type = 'CUDA'
        prefs.get_devices()
        for d in prefs.devices:
            d.use = d.type == 'CUDA'
        scene.cycles.device = 'GPU'
    except Exception:
        scene.cycles.device = 'CPU'
    scene.cycles.use_denoising = True
    scene.render.resolution_x, scene.render.resolution_y = 1280, 720
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    bpy.ops.mesh.primitive_plane_add(size=40)
    bpy.context.active_object.data.materials.append(soil_material(ground_hex))
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = 3.5
    sun.angle = math.radians(1.0)
    so = bpy.data.objects.new('sun', sun)
    eg.link(so)
    so.rotation_euler = (math.radians(45), 0, math.radians(135))
    world = bpy.data.worlds.new('w')
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Color'].default_value = (*eg.hex_rgb('#9FB4D0'), 1)
    world.node_tree.nodes['Background'].inputs['Strength'].default_value = 1.0
    scene.world = world
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    eg.link(cam)
    scene.camera = cam
    cam.data.sensor_fit = 'VERTICAL'
    cam.data.clip_start = 0.005
    return scene, cam


def step_preview(out, assets):
    pdir = os.path.join(out, 'previews')
    os.makedirs(pdir, exist_ok=True)
    made = []
    for name in assets:
        folder = os.path.join(out, name)
        if not os.path.exists(os.path.join(folder, 'manifest.json')):
            continue
        cfg = PREVIEW[name]
        ext = cfg['extent']
        scene, cam = setup_preview_scene(cfg['ground'])
        obj = import_asset(folder, name)
        bpy.context.view_layer.update()
        if cfg.get("sink"):
            h = max((obj.matrix_world @ v.co).z for v in obj.data.vertices)
            obj.location.z -= cfg['sink'] * h
        scale_bar(ext * 0.36, -ext * 0.4, 0.0)
        # сверху
        cam.data.angle = math.radians(50)
        cam.location = (0, 0, ext * 0.5 / math.tan(math.radians(25)) * 1.05)
        cam.rotation_euler = (0, 0, 0)
        p = os.path.join(pdir, name + '_top.png')
        render(scene, p)
        made.append(p)
        # наклонно 30° над горизонтом
        cam.data.angle = math.radians(45)
        dist = ext * 1.25
        el = math.radians(30)
        az = math.radians(-70)
        cam.location = (dist * math.cos(el) * math.cos(az), dist * math.cos(el) * math.sin(az), dist * math.sin(el))
        look_at(cam, (0, 0, 0.0))
        p = os.path.join(pdir, name + '_oblique.png')
        render(scene, p)
        made.append(p)
        # крупно у земли: объектив низко, взгляд на ближайшую к центру часть ассета
        cam.data.angle = math.radians(55)
        mw = obj.matrix_world
        pts = [mw @ v.co for v in obj.data.vertices]
        target = min(pts, key=lambda q: q.x * q.x + q.y * q.y)
        target = Vector((target.x, target.y, 0.0))
        d_close = min(max(0.3, ext * 0.33), 1.1)
        cam.location = target + Vector((0.0, -d_close, d_close * 0.3))
        look_at(cam, target)
        p = os.path.join(pdir, name + '_close.png')
        render(scene, p)
        made.append(p)
        # россыпь пятен с высоты 3 м под 45° (повторы, слияние с землёй)
        if 'scatter' in cfg:
            rng = random.Random(5)
            sp = cfg['scatter']
            for ix in range(-3, 4):
                for iy in range(-1, 7):
                    if ix == 0 and iy == 0:
                        continue
                    if rng.random() < 0.35:
                        continue
                    o = obj.copy()
                    eg.link(o)
                    o.location = (ix * sp + rng.uniform(-0.3, 0.3) * sp, iy * sp + rng.uniform(-0.3, 0.3) * sp, 0)
                    o.rotation_euler = (obj.rotation_euler.x, obj.rotation_euler.y, rng.uniform(0, math.tau))
            cam.data.angle = math.radians(70)
            cam.location = (0, -2.0, 3.0)
            cam.rotation_euler = (math.radians(45), 0, 0)
            p = os.path.join(pdir, name + '_3m.png')
            render(scene, p, 48)
            made.append(p)
    contact_sheet(made, os.path.join(pdir, 'contact_ground.png'))
    return made


def contact_sheet(paths, out_path, cols=4, tile=(480, 270)):
    imgs = []
    for p in paths:
        if not os.path.exists(p):
            continue
        img = bpy.data.images.load(p, check_existing=False)
        w, h = img.size
        px = np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1]
        bpy.data.images.remove(img)
        ys = (np.arange(tile[1]) * h / tile[1]).astype(int)
        xs = (np.arange(tile[0]) * w / tile[0]).astype(int)
        imgs.append(px[ys][:, xs])
    if not imgs:
        return
    rows = (len(imgs) + cols - 1) // cols
    sheet = np.zeros((rows * tile[1], cols * tile[0], 4), np.float32)
    sheet[..., 3] = 1
    for i, im in enumerate(imgs):
        r, c = i // cols, i % cols
        sheet[r * tile[1]:(r + 1) * tile[1], c * tile[0]:(c + 1) * tile[0]] = im
    eg.save_png(sheet, out_path, linear=True)


# ------------------------------------------------------------------ запуск

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=OUT_DEFAULT)
    ap.add_argument('--step', default='all', choices=('wood', 'stone', 'preview', 'all'))
    ap.add_argument('--assets', nargs='*', default=None)
    args = ap.parse_args(eg.script_args())
    out = args.out
    os.makedirs(out, exist_ok=True)
    all_assets = TWIG_ASSETS + BRANCH_ASSETS + PEBBLE_ASSETS + STONE_ASSETS
    assets = args.assets or all_assets
    report = {}
    if args.step in ('wood', 'all'):
        report.update(step_wood(out, [a for a in assets if a in TWIG_ASSETS + BRANCH_ASSETS]))
    if args.step in ('stone', 'all'):
        report.update(step_stone(out, [a for a in assets if a in PEBBLE_ASSETS + STONE_ASSETS]))
    if args.step in ('preview', 'all'):
        step_preview(out, assets)
    if report:
        path = os.path.join(out, 'ground_report.json')
        old = {}
        if os.path.exists(path):
            with open(path, encoding='utf-8') as f:
                old = json.load(f)
        old.update(report)
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(old, f, indent=1)
    print('GROUND_OK', json.dumps(report))


main()
