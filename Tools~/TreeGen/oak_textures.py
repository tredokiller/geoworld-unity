# Текстуры дуба черешчатого (Tools/TreeGen), версия 2.
#
#  - oak_bark   (1024 x 2048, плитка 0.6 x 1.2 м): кора старого ствола — глубокие продольные V-борозды, гребни почти
#    не сплетаются (не ясень), а ломаются поперёк на прямоугольные пластины 7-20 см со своим наклоном, высотой и
#    оттенком; на гребнях пробка, волокна, лишайник, в бороздах бурая и местами рыжая луб. Бесшовная по обеим осям
#    (всё случайное по пластинам берётся по индексу пластины по модулю их числа в плитке).
#  - oak_branch (1024², плитка 0.3 x 0.3 м): кора ветвей тоньше ~11 см — гладкая серая с мелкими продольными
#    трещинками и чечевичками; тон близок к стволу (не белёсая).
#  - oak_leaves (2048², 2x2): смоделированные побеги дуба (симподиальные зигзагообразные годичные приросты, на концах
#    побегов розетки 5-8 листьев), отрендеренные сверху: 0 — большая ветка (световые листья), 1 — большая ветка
#    (теневые и смешанные), 2 — средняя ветка, 3 — малый пучок (2-4 розетки; низ кроны, водяные побеги). Пучки с
#    просветами между ними; старые веточки дорисованы поверх листьев, чтобы зимой (шейдер сезона оставляет только
#    не зелёные тексели) остался связный каркас.
#  - oak_clumps (1024², 2x2 по 512): «подкроны» 2-2.4 м из 10-14 таких веток с запечённым AO и нормалью купола — для
#    LOD2 (вид с воздуха 85-240 м: видны пучки и тёмные провалы, а не отдельные листья).
#
# Выход: PNG — альбедо (sRGB, у карточек альфа, цвет растёкся на всю ячейку), нормали (линейно, OpenGL, +Y вверх),
# маска NatureManufacture (R металличность 0, G AO, B высота, A гладкость), <set>_cells.json.
import json
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
import numpy as np
from mathutils import Matrix, Quaternion, Vector

import envgen_common as eg

BARK_TILE = (0.6, 1.2)     # м: ширина (вокруг ствола, U) и высота (V) плитки коры ствола
BRANCH_TILE = (0.3, 0.3)   # м: плитка коры ветвей
BARK_TARGET = '#59534B'    # средний цвет коры ствола (исследование, раздел 5)
BRANCH_TARGET = '#5E5951'
LEAF_TARGET = '#3F5530'


# ------------------------------------------------------------------ шум и фильтры (numpy, бесшовные)

def periodic_noise(h, w, py, px, seed, octaves=4, persistence=0.5, lac=2):
    """Бесшовный value noise (h, w), базовые периоды py x px клеток (по y и по x)."""
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
    """Бесшовная 1D функция (сумма синусов с целыми частотами), примерно -1..1."""
    rng = np.random.default_rng(seed)
    t = np.arange(n, dtype=np.float32) / n
    amps = amps or [1.0 / f ** 0.9 for f in freqs]
    out = np.zeros(n, np.float32)
    for f, a in zip(freqs, amps):
        out += a * rng.uniform(0.5, 1.0) * np.sin(2 * np.pi * (f * t + rng.random()))
    return out / sum(amps)


def gblur(a, sy, sx=None):
    """Гауссово размытие с заворотом (FFT), sigma в пикселях."""
    sx = sy if sx is None else sx
    h, w = a.shape
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.fftfreq(w)[None, :]
    g = np.exp(-2 * (np.pi ** 2) * ((fy * sy) ** 2 + (fx * sx) ** 2))
    return np.real(np.fft.ifft2(np.fft.fft2(a) * g)).astype(np.float32)


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def colorize(t, stops):
    """Цвет по 0..1: stops — [(t, '#hex')], линейные значения."""
    hs = np.array([s[0] for s in stops], np.float32)
    cs = np.array([eg.hex_rgb(s[1]) for s in stops], np.float32)
    out = np.zeros(t.shape + (3,), np.float32)
    for k in range(3):
        out[..., k] = np.interp(t, hs, cs[:, k])
    return out


def height_to_normal(height_m, texel_m, strength=1.0):
    """Нормаль (OpenGL: +Y — вверх по картинке, строки сверху вниз) из высоты в метрах, 0..1."""
    dx = (np.roll(height_m, -1, 1) - np.roll(height_m, 1, 1)) / (2 * texel_m) * strength
    dy = (np.roll(height_m, 1, 0) - np.roll(height_m, -1, 0)) / (2 * texel_m) * strength
    n = np.stack([-dx, -dy, np.ones_like(height_m)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def cavity_ao(h, sigmas=(3, 10, 30), weights=(0.4, 0.4, 0.3), gain=2.5):
    ao = np.zeros_like(h)
    for s, w in zip(sigmas, weights):
        ao += w * np.maximum(gblur(h, s) - h, 0)
    return np.clip(1.0 - ao * gain, 0.0, 1.0)


def match_mean(col_lin, target_hex, mask=None, strength=1.0):
    """Масштаб цвета так, чтобы средний (линейный) был равен target (по каналам, с силой strength)."""
    sel = col_lin if mask is None else col_lin[mask]
    mean = sel.reshape(-1, 3).mean(0)
    tgt = np.array(eg.hex_rgb(target_hex), np.float32)
    k = (tgt / np.maximum(mean, 1e-5)) ** strength
    return col_lin * k[None, None, :]


def lin_to_srgb_hex(c):
    c = np.clip(c, 0, 1)
    s = np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)
    return '#' + ''.join(f'{int(round(v * 255)):02x}' for v in s)


def dilate_known(rgb, known, iters):
    """Растекание цвета от известных пикселей на iters пикселей (4-соседи)."""
    rgb = rgb.copy()
    known = known.copy()
    for _ in range(iters):
        acc = np.zeros_like(rgb)
        cnt = np.zeros(known.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            sh = np.roll(np.roll(known, dy, 0), dx, 1)
            acc += np.roll(np.roll(rgb, dy, 0), dx, 1) * sh[..., None]
            cnt += sh
        grow = (~known) & (cnt > 0)
        rgb[grow] = acc[grow] / cnt[grow][:, None]
        known |= grow
    return rgb, known


def push_pull_fill(rgb, a, thresh=0.02, near=12):
    """Цвет в прозрачные места: сначала растекание на near пикселей (кайма — цвет соседнего края), дальше пирамида
    (премультипликация), от грубого к тонкому — в мипах нет чёрной каймы."""
    rgb, known = dilate_known(rgb, a > thresh, near)
    a = known.astype(np.float32)
    thresh = 0.5
    w0 = known.astype(np.float32)
    levels = [(rgb * w0[..., None], w0)]
    while min(levels[-1][1].shape) > 1:
        c, w = levels[-1]
        h, ww = w.shape
        h2, w2 = h // 2, ww // 2
        c = c[:h2 * 2, :w2 * 2].reshape(h2, 2, w2, 2, 3).sum((1, 3))
        w = w[:h2 * 2, :w2 * 2].reshape(h2, 2, w2, 2).sum((1, 3))
        levels.append((c, w))
    c, w = levels[-1]
    filled = c / np.maximum(w, 1e-6)[..., None]
    for c, w in reversed(levels[:-1]):
        up = np.repeat(np.repeat(filled, 2, 0), 2, 1)
        h, ww = w.shape
        upf = np.zeros((h, ww, 3), np.float32)
        upf[:up.shape[0], :up.shape[1]] = up[:h, :ww]
        if up.shape[0] < h:
            upf[up.shape[0]:] = upf[up.shape[0] - 1:up.shape[0]]
        if up.shape[1] < ww:
            upf[:, up.shape[1]:] = upf[:, up.shape[1] - 1:up.shape[1]]
        own = c / np.maximum(w, 1e-6)[..., None]
        k = np.clip(w, 0, 1)[..., None]
        filled = own * k + upf * (1 - k)
    out = rgb.copy()
    m = a <= thresh
    out[m] = filled[m]
    return out


def box_down(rgba, f):
    """Уменьшение в f раз: rgb = sum(rgb a)/sum(a), a = mean(a)."""
    h, w = rgba.shape[:2]
    r = rgba[:h // f * f, :w // f * f].reshape(h // f, f, w // f, f, rgba.shape[2])
    a = r[..., 3]
    asum = a.sum((1, 3))
    rgb = (r[..., :3] * a[..., None]).sum((1, 3)) / np.maximum(asum, 1e-6)[..., None]
    rgb_plain = r[..., :3].mean((1, 3))
    rgb = np.where(asum[..., None] > 1e-6, rgb, rgb_plain)
    return np.concatenate([rgb, (asum / (f * f))[..., None]], -1)


def box_down_plain(arr, f):
    h, w = arr.shape[:2]
    c = arr.shape[2] if arr.ndim == 3 else 1
    r = arr[:h // f * f, :w // f * f].reshape(h // f, f, w // f, f, c)
    return r.mean((1, 3))


def _save_set(out_dir, prefix, albedo_lin, normal, mask):
    os.makedirs(out_dir, exist_ok=True)
    names = [prefix + '_albedo.png', prefix + '_normal.png', prefix + '_mask.png']
    eg.save_png(albedo_lin, os.path.join(out_dir, names[0]))
    eg.save_png(normal, os.path.join(out_dir, names[1]), linear=True)
    eg.save_png(mask, os.path.join(out_dir, names[2]), linear=True)
    return names


# ------------------------------------------------------------------ кора ствола

def _fissure_lines(H, n, seed, amp=(0.22, 0.42), hw=(0.009, 0.015), hw_var=0.45):
    """Продольные борозды: положения (n, H) в долях ширины и полуширины (м). Почти не пересекаются."""
    rng = np.random.default_rng(seed)
    base = (np.arange(n) + rng.uniform(-0.22, 0.22, n)) / n
    lines, half = [], []
    for i in range(n):
        m = periodic_1d(H, seed * 100 + i, freqs=(1, 2, 3, 4, 6, 9), amps=[1.0, 0.8, 0.55, 0.4, 0.22, 0.1])
        wig = periodic_1d(H, seed * 100 + i + 50, freqs=(19, 31, 53, 79), amps=[1.0, 0.6, 0.4, 0.25]) * 0.0022
        lines.append(base[i] + rng.uniform(*amp) / n * m + wig)
        half.append(rng.uniform(*hw) * np.clip(1.0 + hw_var * periodic_1d(H, seed * 100 + i + 70, freqs=(2, 3, 5, 8, 13)),
                                               0.35, 1.8))
    return np.array(lines, np.float32), np.array(half, np.float32)


def _ridge_field(W, H, lines, half, TW, rag_noise):
    """Для каждого пикселя: номер гребня (между соседними бороздами), расстояния до левой и правой борозды (м) и их
    полуширины. Линии сортируются в каждой строке — номер гребня не меняется там, где борозды сходятся."""
    n = lines.shape[0]
    L = np.concatenate([lines - 1, lines, lines + 1], 0)
    Hh = np.concatenate([half, half, half], 0)
    order = np.argsort(L, axis=0)
    Ls = np.take_along_axis(L, order, 0)
    Hs = np.take_along_axis(Hh, order, 0)
    slope = (np.roll(Ls, -1, 1) - np.roll(Ls, 1, 1)) * H / 2.0 * (TW / BARK_TILE[1])
    cosk = 1.0 / np.sqrt(1.0 + slope ** 2)
    x = (np.arange(W, dtype=np.float32) + 0.5) / W
    xr = x[None, :] + rag_noise
    k = np.clip(np.sum(Ls[:, :, None] <= xr[None], axis=0) - 1, 0, 3 * n - 2)
    yi = np.arange(H)[:, None]
    dl = (xr - Ls[k, yi]) * TW * cosk[k, yi]
    dr = (Ls[k + 1, yi] - xr) * TW * cosk[k + 1, yi]
    return k % n, dl, dr, Hs[k, yi], Hs[k + 1, yi]


def bark_trunk(out_dir, W=1024, H=2048, seed=31, prefix='oak_bark'):
    """Кора старого дуба черешчатого (плитка 0.6 x 1.2 м)."""
    TW, TH = BARK_TILE
    texel = TW / W
    rng = np.random.default_rng(seed)
    n = 9                                    # борозды через ~6.7 см
    lines, half = _fissure_lines(H, n, seed, hw=(0.015, 0.026), hw_var=0.55)
    rag = (periodic_noise(H, W, 96, 48, seed + 1, 3, 0.6) - 0.5) * 0.016 \
        + (periodic_noise(H, W, 400, 200, seed + 2, 2, 0.5) - 0.5) * 0.006
    rid, dl, dr, hl, hr = _ridge_field(W, H, lines, half, TW, rag)
    Rw = np.maximum(dl + dr, 1e-4)           # ширина гребня с бороздами (м)
    # профиль: V-борозда (узкое тёмное дно, стенки), гребень выпуклый, с обломанными краями
    wl = smoothstep(0.0, 1.0, np.clip(dl / hl, 0, 1)) ** 0.9
    wr = smoothstep(0.0, 1.0, np.clip(dr / hr, 0, 1)) ** 0.9
    wall = np.minimum(wl, wr)
    centre = 1.0 - np.abs(dl - dr) / Rw
    h = wall * (0.72 + 0.28 * np.sqrt(np.clip(centre, 0, 1)))
    # узкие гребни (где борозды сходятся) ниже
    h *= 0.45 + 0.55 * smoothstep(0.012, 0.045, Rw)
    # широкие гребни: неглубокая продольная трещина посередине, местами
    split_on = smoothstep(0.5, 0.7, periodic_noise(H, W, 6, 9, seed + 3, 3)) * smoothstep(0.05, 0.075, Rw)
    dmid = np.abs(dl - dr) * 0.5
    h *= 1.0 - 0.45 * split_on * (1 - smoothstep(0.0, 0.004, dmid))
    # поперечные разломы: пластины по гребням
    yy = (np.arange(H, dtype=np.float32)[:, None] + 0.5) / H
    n_pl = rng.integers(4, 11, n)            # пластин на плитку (1.2 м): 11-30 см
    ph0 = rng.random(n)
    mod_a = rng.uniform(0.05, 0.18, n)
    mod_f = rng.integers(1, 3, n)
    mod_p = rng.random(n)
    tilt = rng.uniform(-0.8, 0.8, n)
    t_across = (dl - dr) / Rw                # -1..1 поперёк гребня
    warp = (periodic_noise(H, W, 40, 20, seed + 4, 3) - 0.5) * 0.03
    yp = yy + tilt[rid] * t_across * (Rw / TH) * 0.5 + warp
    arg = 2 * np.pi * (mod_f[rid] * yp + mod_p[rid])
    phi = n_pl[rid] * yp + ph0[rid] + mod_a[rid] * np.sin(arg)
    dphi = n_pl[rid] + mod_a[rid] * 2 * np.pi * mod_f[rid] * np.cos(arg)
    dcr = np.abs(phi - np.round(phi)) / np.maximum(np.abs(dphi), 0.5) * TH  # м до трещины
    tab = np.random.default_rng(seed + 8).random((n, 32, 8)).astype(np.float32)
    jc = np.round(phi).astype(int) % n_pl[rid]
    cr_depth = 0.55 + 0.45 * tab[rid, jc, 0]
    cr_w = 0.006 + 0.012 * tab[rid, jc, 1]
    # часть трещин — не через весь гребень (от одной борозды)
    part = tab[rid, jc, 2]
    side = np.where(tab[rid, jc, 3] > 0.5, t_across, -t_across)
    reach = np.where(part < 0.45, smoothstep(0.15 + part, -0.45 + part, side), 1.0)
    crack = 1.0 - (1.0 - smoothstep(0.0, 1.0, dcr / cr_w)) * cr_depth * reach
    jp = np.floor(phi).astype(int) % n_pl[rid]
    fr = phi - np.floor(phi)                 # 0..1 вдоль пластины
    pl_h = 0.82 + 0.22 * tab[rid, jp, 4]
    pl_tilt = (tab[rid, jp, 5] - 0.5) * 0.35
    h = h * crack * (pl_h + pl_tilt * (fr - 0.5)) * (1 - 0.12 * (1 - smoothstep(0.0, 0.3, fr)))
    # мелкие трещинки по верху гребней (короткие, продольные и косые), пробка, волокна, чешуйки
    top = smoothstep(0.45, 0.9, h)
    micro = periodic_noise(H, W, 24, 70, seed + 22, 3, 0.55)
    microc = 1 - smoothstep(0.0, 0.035, np.abs(micro - 0.5))
    microc *= smoothstep(0.45, 0.7, periodic_noise(H, W, 20, 20, seed + 23, 2))
    cork = periodic_noise(H, W, 80, 40, seed + 21, 4, 0.6)
    fib = periodic_noise(H, W, 6, 160, seed + 13, 3, 0.55)
    fine = periodic_noise(H, W, 300, 150, seed + 14, 2, 0.6)
    flakes = smoothstep(0.58, 0.76, periodic_noise(H, W, 60, 40, seed + 15, 2))
    h = h * (1 - 0.28 * top * (1 - cork)) * (0.92 + 0.08 * fib) * (0.94 + 0.06 * fine) - 0.07 * flakes * top \
        - 0.18 * microc * top
    h = np.clip(h, 0, 1)
    # цвет
    col = colorize(h, [(0.0, '#221c18'), (0.12, '#2b241f'), (0.35, '#3a322b'), (0.62, '#51493f'),
                       (0.78, '#655e55'), (0.92, '#736c63'), (1.0, '#7e786e')])
    inner = smoothstep(0.55, 0.75, periodic_noise(H, W, 12, 16, seed + 19, 3)) * (1 - smoothstep(0.05, 0.22, h))
    col += (np.array(eg.hex_rgb('#4a3324'), np.float32) - col) * (inner * 0.35)[..., None]
    tint = tab[rid, jp, 6]
    brown = np.array(eg.hex_rgb('#6a5c4e'), np.float32)
    grey = np.array(eg.hex_rgb('#7f7a71'), np.float32)
    tmix = np.where(tint[..., None] < 0.35, brown, grey)
    col += (tmix - col) * (top * np.abs(tint - 0.35) * 0.6)[..., None]
    col *= (0.9 + 0.2 * cork * top + 0.1 * (1 - top))[..., None]
    col *= (0.94 + 0.12 * fib)[..., None]
    lichen = smoothstep(0.66, 0.76, periodic_noise(H, W, 10, 5, seed + 17, 4)) * top
    lichen *= smoothstep(0.35, 0.65, periodic_noise(H, W, 80, 40, seed + 18, 2))
    col += (np.array(eg.hex_rgb('#8b917c'), np.float32) - col) * (lichen * 0.55)[..., None]
    algae = smoothstep(0.62, 0.8, periodic_noise(H, W, 8, 4, seed + 20, 4)) * smoothstep(0.25, 0.7, h)
    col += (np.array(eg.hex_rgb('#4f5732'), np.float32) - col) * (algae * 0.3)[..., None]
    ao = cavity_ao(h, sigmas=(4, 14, 40), gain=2.4)
    col *= (0.62 + 0.38 * ao)[..., None]
    col = match_mean(col, BARK_TARGET)
    depth_m = 0.035
    hm = h * depth_m + (cork - 0.5) * 0.0015 * top + (fine - 0.5) * 0.0006
    normal = height_to_normal(hm, texel, 1.0)
    smooth = 0.05 + 0.13 * smoothstep(0.3, 0.95, h) - 0.03 * lichen
    mask = np.stack([np.zeros_like(h), ao, h, np.clip(smooth, 0, 1)], -1)
    print('BARK mean', lin_to_srgb_hex(col.reshape(-1, 3).mean(0)))
    return _save_set(out_dir, prefix, col, normal, mask)


def bark_branch(out_dir, S=1024, seed=41, prefix='oak_branch'):
    """Кора ветвей (моложе ~30 лет): гладкая серо-оливковая, мелкие продольные трещинки, чечевички, лишайник точками."""
    T = BRANCH_TILE[0]
    texel = T / S
    rng = np.random.default_rng(seed)
    x = (np.arange(S, dtype=np.float32) + 0.5) / S
    h = np.ones((S, S), np.float32)
    n_lines = 22
    base = (np.arange(n_lines) + rng.uniform(-0.4, 0.4, n_lines)) / n_lines
    for i in range(n_lines):
        m = periodic_1d(S, seed * 10 + i, freqs=(1, 2, 3, 5), amps=[1.0, 0.7, 0.4, 0.2])
        xi = (base[i] + m * rng.uniform(0.3, 0.8) / n_lines) % 1.0
        d = np.abs(((x[None, :] - xi[:, None]) + 0.5) % 1.0 - 0.5) * T
        seg = smoothstep(0.42, 0.62, periodic_noise(S, S, 7, 1, seed + 100 + i, 3))
        w = 0.0009 + 0.0014 * periodic_noise(S, S, 9, 1, seed + 200 + i, 2)
        h -= (1 - smoothstep(0.0, w, d)) * seg * rng.uniform(0.3, 0.65)
    h = np.clip(h, 0, 1)
    wr = periodic_noise(S, S, 8, 60, seed + 3, 3, 0.55)
    rough = periodic_noise(S, S, 60, 60, seed + 4, 3, 0.6)
    h = np.clip(h * (0.93 + 0.07 * wr) + 0.03 * rough, 0, 1)
    lent = np.zeros((S, S), np.float32)
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    for _ in range(170):
        cx, cy = rng.uniform(0, S), rng.uniform(0, S)
        lw, lh = rng.uniform(3, 8), rng.uniform(1.0, 2.0)
        dx = (xx - cx + S / 2) % S - S / 2
        dy = (yy - cy + S / 2) % S - S / 2
        lent = np.maximum(lent, np.clip(1 - (dx / lw) ** 2 - (dy / lh) ** 2, 0, 1))
    lent = smoothstep(0.0, 0.5, lent)
    h = np.clip(h + 0.08 * lent, 0, 1)
    big = periodic_noise(S, S, 3, 2, seed + 5, 4)
    lichen = smoothstep(0.64, 0.74, periodic_noise(S, S, 7, 5, seed + 6, 4)) * \
        smoothstep(0.4, 0.7, periodic_noise(S, S, 50, 50, seed + 7, 2))
    col = colorize(h, [(0.0, '#2a2622'), (0.55, '#48433d'), (0.85, '#5b564f'), (1.0, '#66615a')])
    col *= (0.9 + 0.2 * big)[..., None]
    col *= (0.96 + 0.08 * wr)[..., None]
    col += (np.array(eg.hex_rgb('#8e897b'), np.float32) - col) * (lent * 0.45)[..., None]
    col += (np.array(eg.hex_rgb('#7f8670'), np.float32) - col) * (lichen * 0.4)[..., None]
    col += (np.array(eg.hex_rgb('#545a3e'), np.float32) - col) * (smoothstep(0.68, 0.85, big) * 0.12)[..., None]
    ao = cavity_ao(h, sigmas=(2, 6, 16), gain=3.5)
    col *= (0.75 + 0.25 * ao)[..., None]
    col = match_mean(col, BRANCH_TARGET)
    normal = height_to_normal(h * 0.0022, texel, 1.0)
    smooth = 0.14 + 0.08 * h
    mask = np.stack([np.zeros_like(h), ao, h, smooth], -1)
    print('BRANCH mean', lin_to_srgb_hex(col.reshape(-1, 3).mean(0)))
    return _save_set(out_dir, prefix, col, normal, mask)


# ------------------------------------------------------------------ лист дуба (numpy)

LEAF_X = 0.34   # полуширина кадра листа (доля длины)
LEAF_Y0 = -0.01
LEAF_Y1 = 1.01
LEAF_CELL = (320, 480)  # пикселей на лист в листе текстур (ширина, высота)


def _sd_ellipse(px, py, cx, cy, ra, rb, ang):
    s, c = math.sin(ang), math.cos(ang)
    dx, dy = px - cx, py - cy
    u = dx * s + dy * c
    v = dx * c - dy * s
    k = np.sqrt((u / ra) ** 2 + (v / rb) ** 2)
    return (k - 1.0) * min(ra, rb)


def _smin(a, b, k):
    h = np.clip(0.5 + 0.5 * (b - a) / k, 0, 1)
    return b * (1 - h) + a * h - k * h * (1 - h)


def _seg_dist(px, py, ax, ay, bx, by):
    abx, aby = bx - ax, by - ay
    t = np.clip(((px - ax) * abx + (py - ay) * aby) / max(abx * abx + aby * aby, 1e-12), 0, 1)
    return np.hypot(px - ax - abx * t, py - ay - aby * t), t


def leaf_shape(rng, kind):
    """Обратнояйцевидный лист Q. robur: 4-6 закруглённых лопастей на сторону, выемки до 1/3-1/2, ушки у основания,
    черешок 2-7 мм. kind: 'sun' | 'shade' | 'damaged'."""
    sun = kind != 'shade'
    p = rng.uniform(0.025, 0.05)
    wmax = rng.uniform(0.25, 0.29) if sun else rng.uniform(0.28, 0.32)
    s_peak = rng.uniform(0.6, 0.7)
    a = math.log(0.5) / math.log(s_peak)
    depth = rng.uniform(0.42, 0.56) if sun else rng.uniform(0.3, 0.42)
    n = int(rng.choice([4, 5, 5, 6]) if sun else rng.choice([4, 5, 5]))
    lobes = []
    for side in (-1, 1):
        shift = rng.uniform(0.15, 0.5) * (1 if side > 0 else 0)
        for s in np.linspace(0.14, 0.84, n) + (shift - 0.25) * (0.7 / n):
            s = float(np.clip(s + rng.uniform(-0.03, 0.03), 0.08, 0.88))
            lobes.append(dict(side=side, s=s, grow=rng.uniform(0.85, 1.1),
                              ang=math.radians(rng.uniform(52, 72) - 14 * s), width=rng.uniform(0.78, 0.98)))
    holes, bites = [], []
    if kind == 'damaged':
        holes = [(rng.uniform(-0.1, 0.1), rng.uniform(0.3, 0.8), rng.uniform(0.022, 0.045), rng.random() * 10)
                 for _ in range(int(rng.integers(1, 4)))]
        bites = [(int(rng.choice([-1, 1])), rng.uniform(0.35, 0.85), rng.uniform(0.035, 0.07), rng.random() * 10)
                 for _ in range(int(rng.integers(1, 3)))]
    return dict(p=p, wmax=wmax, a=a, depth=depth, n=n, lobes=lobes,
                term=(rng.uniform(0.09, 0.12), rng.uniform(0.11, 0.15), rng.uniform(-0.02, 0.02)),
                aur=rng.uniform(0.02, 0.027), kind=kind, holes=holes, bites=bites)


def _env_at(sh, s):
    return sh['wmax'] * np.sin(np.pi * np.clip(s, 0, 1) ** sh['a']) ** 0.62


def _blob(px, py, cx, cy, r, ph):
    """Неровное пятно (дырка, погрыз): радиус плавно меняется с углом (без звёздочек)."""
    ang = np.arctan2(py - cy, px - cx)
    rr = r * (1 + 0.18 * np.sin(ang + ph) + 0.1 * np.sin(2 * ang + ph * 1.7))
    return np.hypot(px - cx, py - cy) - rr


def leaf_sdf(sh, px, py):
    """Знаковое расстояние до контура листа (отрицательное внутри), в долях длины."""
    s = np.clip((py - sh['p']) / (1 - sh['p']), 0, 1)
    E = _env_at(sh, s)
    C = E * (1 - sh['depth'])
    core = (np.abs(px) - C) * 0.9
    core = np.maximum(core, np.maximum(sh['p'] + 0.01 - py, py - 0.96))
    d = core
    spacing = 0.7 / sh['n'] * (1 - sh['p'])
    for lb in sh['lobes']:
        yk = sh['p'] + lb['s'] * (1 - sh['p'])
        Ek = float(_env_at(sh, lb['s'])) * lb['grow']
        Ck = Ek * (1 - sh['depth'])
        ang = lb['ang']
        rb = 0.5 * spacing * lb['width'] * 1.05
        ra = (Ek - Ck) / max(math.sin(ang), 0.3) * 0.62 + rb * 0.45
        tipx, tipy = lb['side'] * Ek, yk + 0.25 * spacing
        ux, uy = lb['side'] * math.sin(ang), math.cos(ang)
        cx, cy = tipx - ux * ra, tipy - uy * ra
        d = _smin(d, _sd_ellipse(px, py, cx, cy, ra, rb, lb['side'] * ang), 0.022)
    tx, ty, tsh = sh['term']
    d = _smin(d, _sd_ellipse(px, py, tsh, 1.0 - ty, ty, tx, 0.0), 0.03)
    ar = sh['aur']
    for side in (-1, 1):
        d = _smin(d, np.hypot(px - side * ar * 0.75, py - (sh['p'] + ar * 1.1)) - ar, 0.03)
    pet = np.maximum(np.abs(px) - 0.0065, np.maximum(-py, py - sh['p'] - 0.04))
    d = np.minimum(d, pet)
    for hx, hy, hr, ph in sh['holes']:
        d = np.maximum(d, -_blob(px, py, hx, hy, hr, ph))
    for side, by, br, ph in sh['bites']:
        Eb = float(_env_at(sh, (by - sh['p']) / (1 - sh['p'])))
        d = np.maximum(d, -_blob(px, py, side * Eb * 1.0, by, br, ph))
    return d


def leaf_veins(sh, px, py, seed=0):
    mid, t = _seg_dist(px, py, 0.0, 0.0, sh['term'][2] * 0.6, 0.97)
    midrib = 1 - smoothstep(0.0, 0.0105 * (1 - t) + 0.0035, mid)
    sec = np.zeros_like(px)
    spacing = 0.7 / sh['n'] * (1 - sh['p'])
    lobes = sorted(sh['lobes'], key=lambda l: (l['side'], l['s']))
    for i, lb in enumerate(lobes):
        yk = sh['p'] + lb['s'] * (1 - sh['p'])
        Ek = float(_env_at(sh, lb['s'])) * lb['grow']
        tipx, tipy = lb['side'] * Ek * 0.88, yk + 0.22 * spacing
        y0 = yk - 0.12 - 0.05 * lb['s']
        mx, my = lb['side'] * Ek * 0.35, (y0 + tipy) * 0.5 - 0.01
        pts = [(0.0, y0)] + [(2 * (1 - u) * u * mx + u * u * tipx, (1 - u) ** 2 * y0 + 2 * (1 - u) * u * my + u * u * tipy)
                             for u in (0.25, 0.5, 0.75, 1.0)]
        for j, ((ax, ay), (bx, by)) in enumerate(zip(pts[:-1], pts[1:])):
            dd, _ = _seg_dist(px, py, ax, ay, bx, by)
            sec = np.maximum(sec, 1 - smoothstep(0.0, 0.0048 - 0.0007 * j, dd))
        if i > 0 and lobes[i - 1]['side'] == lb['side']:
            ys = (yk + sh['p'] + lobes[i - 1]['s'] * (1 - sh['p'])) * 0.5
            Es = float(_env_at(sh, (ys - sh['p']) / (1 - sh['p']))) * (1 - sh['depth'])
            dd, _ = _seg_dist(px, py, 0.0, ys - 0.07, lb['side'] * Es * 0.8, ys)
            sec = np.maximum(sec, 0.5 * (1 - smoothstep(0.0, 0.003, dd)))
    tert = periodic_noise(px.shape[0], px.shape[1], 22, 14, seed, 3, 0.6)
    tert = (1 - smoothstep(0.0, 0.05, np.abs(tert - 0.5))) * 0.2
    return np.clip(np.maximum(np.maximum(midrib, sec * 0.85), tert), 0, 1), midrib


def leaf_sheet(seed=3, variants=None):
    """Лист текстур листьев: варианты в ряд; ряд 0 — верх, ряд 1 — изнанка (зеркально). Линейные значения, строки сверху
    вниз: albedo (RGBA), height (0..1, гладкая), smooth."""
    variants = variants or ['sun', 'sun', 'sun', 'sun', 'shade', 'shade', 'damaged', 'damaged']
    W, H = LEAF_CELL
    nv = len(variants)
    rng = np.random.default_rng(seed)
    alb = np.zeros((2 * H, nv * W, 4), np.float32)
    hgt = np.full((2 * H, nv * W), 0.5, np.float32)
    smo = np.zeros((2 * H, nv * W), np.float32)
    ss = 2
    yy, xx = np.mgrid[0:H * ss, 0:W * ss].astype(np.float32)
    PX = (xx + 0.5) / (W * ss) * 2 * LEAF_X - LEAF_X
    PY = LEAF_Y1 - (yy + 0.5) / (H * ss) * (LEAF_Y1 - LEAF_Y0)
    yy1, xx1 = np.mgrid[0:H, 0:W].astype(np.float32)
    px1 = (xx1 + 0.5) / W * 2 * LEAF_X - LEAF_X
    py1 = LEAF_Y1 - (yy1 + 0.5) / H * (LEAF_Y1 - LEAF_Y0)
    texel = (LEAF_Y1 - LEAF_Y0) / (H * ss)
    for i, kind in enumerate(variants):
        sh = leaf_shape(rng, kind)
        a = np.clip(0.5 - leaf_sdf(sh, PX, PY) / texel, 0, 1).reshape(H, ss, W, ss).mean(axis=(1, 3))
        d1 = leaf_sdf(sh, px1, py1)
        veins, mid = leaf_veins(sh, px1, py1, seed * 10 + i)
        mottle = periodic_noise(H, W, 6, 4, seed * 10 + i, 4)
        fine = periodic_noise(H, W, 40, 26, seed * 10 + i + 5, 2)
        edge = smoothstep(-0.025, 0.0, d1)
        base = {'sun': ('#34492a', '#3f5530'), 'shade': ('#425c31', '#4c6636'), 'damaged': ('#37502a', '#42572f')}[kind]
        c0 = np.array(eg.hex_rgb(base[0]), np.float32)
        c1 = np.array(eg.hex_rgb(base[1]), np.float32)
        top = c0 + (c1 - c0) * mottle[..., None]
        top *= (0.94 + 0.12 * fine)[..., None]
        top += (np.array(eg.hex_rgb('#6f8443'), np.float32) - top) * np.clip(veins * 0.35 + mid * 0.35, 0, 1)[..., None]
        top += (np.array(eg.hex_rgb('#44532a'), np.float32) - top) * (0.22 * edge)[..., None]
        if kind == 'damaged':  # бурая некротическая кайма у дырок и погрызов
            rim = np.zeros_like(d1)
            for hx, hy, hr, ph in sh['holes']:
                rim = np.maximum(rim, 1 - smoothstep(0.0, 0.016, np.abs(_blob(px1, py1, hx, hy, hr, ph))))
            for side, by, br, ph in sh['bites']:
                Eb = float(_env_at(sh, (by - sh['p']) / (1 - sh['p'])))
                rim = np.maximum(rim, 1 - smoothstep(0.0, 0.02, np.abs(_blob(px1, py1, side * Eb, by, br, ph))))
            top += (np.array(eg.hex_rgb('#5c5530'), np.float32) - top) * (rim * 0.65)[..., None]
        und = np.array(eg.hex_rgb('#5c6f55'), np.float32) * (0.94 + 0.1 * mottle)[..., None]
        und += (np.array(eg.hex_rgb('#76846a'), np.float32) - und) * np.clip(veins * 0.5 + mid * 0.3, 0, 1)[..., None]
        x0 = i * W
        alb[:H, x0:x0 + W, :3] = top
        alb[:H, x0:x0 + W, 3] = a
        alb[H:, x0:x0 + W, :3] = und[:, ::-1]
        alb[H:, x0:x0 + W, 3] = a[:, ::-1]
        # высота для bump: гладкая (без ступенек — иначе грани «как сложенная бумага»), жилки вдавлены сверху
        hv = gblur(veins, 1.2)
        puck = periodic_noise(H, W, 10, 7, seed * 10 + i + 9, 3)
        hgt[:H, x0:x0 + W] = 0.55 - 0.3 * hv + 0.1 * (puck - 0.5)
        hgt[H:, x0:x0 + W] = (0.45 + 0.35 * hv + 0.06 * (puck - 0.5))[:, ::-1]
        smo[:H, x0:x0 + W] = 0.46 - 0.14 * veins
        smo[H:, x0:x0 + W] = 0.18
    alb[..., :3] = push_pull_fill(alb[..., :3], alb[..., 3])
    return dict(albedo=alb, height=hgt, smooth=smo, cell=(W, H), count=nv, variants=variants)


# ------------------------------------------------------------------ побеги с листьями (Blender)

TWIG_NEW = '#7a6248'   # побег этого года: оливково-бурый (G < R: шейдер сезона оставит его зимой)
TWIG_OLD = '#6c675d'   # 2-5-летние веточки: серые
BUD = '#5a412e'


def _np_image(name, arr, non_color):
    h, w = arr.shape[:2]
    if arr.ndim == 2:
        arr = arr[..., None]
    if arr.shape[2] == 1:
        arr = np.concatenate([arr, arr, arr, np.ones_like(arr)], 2)
    elif arr.shape[2] == 3:
        arr = np.concatenate([arr, np.ones((h, w, 1), np.float32)], 2)
    img = bpy.data.images.new(name, w, h, alpha=True, float_buffer=True)
    img.colorspace_settings.name = 'Non-Color' if non_color else 'Linear Rec.709'
    img.pixels.foreach_set(np.ascontiguousarray(arr[::-1], np.float32).ravel())
    img.pack()
    return img


def _srgb01(hexcol):
    h = hexcol.lstrip('#')
    return tuple(int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))


def _lin_to_srgb(x):
    return 12.92 * x if x <= 0.0031308 else 1.055 * x ** (1 / 2.4) - 0.055


def _rot(v, axis, ang):
    v = Vector(v)
    a = Vector(axis)
    if a.length < 1e-9:
        return v
    v.rotate(Quaternion(a.normalized(), ang))
    return v


class Spray:
    """Ветка дуба для атласа. Годичные приросты: из пучка почек на конце прироста следующей весной 1-3 побега
    (продолжение с изломом 8-25° попеременно в стороны — зигзаг, боковые 35-70°), часть почек не распускается; на конце
    побега этого года — розетка 5-8 листьев (спираль 2/5 на последних 2-4 см), листья почти горизонтально (к камере,
    +Z). Розетки не налезают друг на друга — между пучками просветы, как у дуба."""

    def __init__(self, rng, sheet, leaf_kinds, leaf_len, under_p=0.03, min_gap=0.11):
        self.rng = rng
        self.sheet = sheet
        self.kinds = leaf_kinds
        self.leaf_len = leaf_len
        self.under_p = under_p
        self.min_gap = min_gap
        self.leaves = eg.MeshBuilder()
        self.twig_new = eg.MeshBuilder()
        self.twig_old = eg.MeshBuilder()
        self.rosettes = []

    def tube(self, mb, a, b, ra, rb, color, sides=6):
        a, b = Vector(a), Vector(b)
        ax = b - a
        if ax.length < 1e-5:
            return
        ax.normalize()
        nx = ax.orthogonal().normalized()
        ny = ax.cross(nx).normalized()
        c = (*_srgb01(color), 1.0)
        r0, r1 = [], []
        for s in range(sides):
            ang = s / sides * math.tau
            off = nx * math.cos(ang) + ny * math.sin(ang)
            r0.append(mb.vert(a + off * ra))
            r1.append(mb.vert(b + off * rb))
        for s in range(sides):
            mb.face([r0[s], r0[(s + 1) % sides], r1[(s + 1) % sides], r1[s]], 0, None, None, [c] * 4)
        # торец (сфера-колпачок, чтобы конец веточки не выглядел срезанным)
        tip = mb.vert(b + ax * rb * 0.8)
        for s in range(sides):
            mb.face([r1[s], r1[(s + 1) % sides], tip], 0, None, None, [c] * 3)

    def bud_cluster(self, p, d):
        for k in range(4):
            dd = (Vector(d) + Vector((self.rng.uniform(-0.7, 0.7), self.rng.uniform(-0.7, 0.7), 0.3))).normalized()
            self.tube(self.twig_new, p, Vector(p) + dd * 0.006, 0.0018, 0.0005, BUD, sides=5)

    def leaf(self, base, fwd, up, length, variant, underside):
        rng = self.rng
        fwd = Vector(fwd).normalized()
        up = (Vector(up) - fwd * Vector(up).dot(fwd)).normalized()
        side = fwd.cross(up).normalized()
        nv = self.sheet['count']
        fold = rng.uniform(0.1, 0.32)
        cup = rng.uniform(-0.2, 0.5)
        arch = rng.uniform(-0.04, 0.1)
        droop = rng.uniform(0.0, 0.14)
        wave = rng.uniform(0.0, 0.035)
        twist = rng.uniform(-0.22, 0.22)
        b = rng.uniform(0.84, 1.05)
        r = rng.random()
        if r < 0.35:      # чуть желтее
            tint = (b * rng.uniform(1.0, 1.1), b, b * rng.uniform(0.82, 0.95))
        elif r < 0.7:     # чуть синее (оливково-сизый)
            tint = (b * rng.uniform(0.88, 0.97), b, b * rng.uniform(1.0, 1.12))
        else:
            tint = (b, b, b)
        tcol = tuple(_lin_to_srgb(min(t, 1.0)) for t in tint) + (1.0,)
        nx, ny = 7, 9
        idx = []
        for j in range(ny):
            y = LEAF_Y0 + (LEAF_Y1 - LEAF_Y0) * j / (ny - 1)
            row = []
            for i in range(nx):
                x = -LEAF_X + 2 * LEAF_X * i / (nx - 1)
                z = fold * abs(x) + cup * x * x + arch * math.sin(math.pi * min(max(y, 0), 1)) - droop * y * y \
                    + wave * math.sin(y * 19 + x * 7) * abs(x) * 3 + twist * x * y
                p = Vector(base) + fwd * (y * length) + side * (x * length) + up * (z * length)
                if underside:
                    u = (variant + (0.5 - x / (2 * LEAF_X))) / nv
                    v = 0.5 * (y - LEAF_Y0) / (LEAF_Y1 - LEAF_Y0)
                else:
                    u = (variant + (x / (2 * LEAF_X) + 0.5)) / nv
                    v = 0.5 + 0.5 * (y - LEAF_Y0) / (LEAF_Y1 - LEAF_Y0)
                row.append((self.leaves.vert(p), (u, v)))
            idx.append(row)
        for j in range(ny - 1):
            for i in range(nx - 1):
                a, b_, c, d = idx[j][i], idx[j][i + 1], idx[j + 1][i + 1], idx[j + 1][i]
                self.leaves.face([a[0], b_[0], c[0], d[0]], 0, [a[1], b_[1], c[1], d[1]], None, [tcol] * 4)

    def rosette(self, tip, d, n=None, scale=1.0):
        rng = self.rng
        d = Vector(d).normalized()
        n = n or int(rng.choice([5, 6, 6, 7, 7, 8]))
        perp = d.cross(Vector((0, 0, 1)))
        if perp.length < 1e-4:
            perp = Vector((1, 0, 0))
        perp.normalize()
        az0 = rng.uniform(0, math.tau)
        for j in range(n):
            f = j / max(n - 1, 1)
            base = Vector(tip) - d * (0.03 * (1 - f)) + Vector((0, 0, rng.uniform(-0.004, 0.004)))
            az = az0 + j * math.radians(144) + rng.uniform(-0.35, 0.35)
            alpha = math.radians(rng.uniform(35, 72) * (1.05 - 0.35 * f))
            dirv = _rot(_rot(d, perp, alpha), d, az)
            dirv.z = dirv.z * 0.3 + rng.uniform(-0.12, 0.12)
            dirv.normalize()
            up = _rot(Vector((0, 0, 1)), dirv, rng.uniform(-0.5, 0.5))
            length = rng.uniform(*self.leaf_len) * (0.78 + 0.32 * f) * scale
            self.leaf(base, dirv, up, length, int(rng.choice(self.kinds)), rng.random() < self.under_p)
        self.bud_cluster(tip, d)
        self.rosettes.append(Vector(tip))

    def _free(self, p):
        return all((p - q).length > self.min_gap for q in self.rosettes)

    def shoot(self, p, d, year, years, length, radius, zig, branch_p=(0.25, 0.5, 0.25)):
        rng = self.rng
        d = Vector(d).normalized()
        e = Vector(p) + d * length
        perp = d.cross(Vector((0, 0, 1)))
        perp = perp.normalized() if perp.length > 1e-6 else Vector((1, 0, 0))
        mid = (Vector(p) + e) * 0.5 + perp * (length * rng.uniform(-0.05, 0.05)) + Vector((0, 0, rng.uniform(-0.008, 0.008)))
        r_end = radius * (0.82 if year < years else 0.6)
        last = year >= years
        mb = self.twig_new if year >= years - 1 else self.twig_old
        color = TWIG_NEW if year >= years - 1 else TWIG_OLD
        self.tube(mb, p, mid, radius, (radius + r_end) * 0.5, color)
        self.tube(mb, mid, e, (radius + r_end) * 0.5, r_end, color)
        d_end = (e - mid).normalized()
        if last:
            if self._free(e):
                self.rosette(e, d_end)
            return
        u = rng.random()
        n = 1 if u < branch_p[0] else (2 if u < branch_p[0] + branch_p[1] else 3)
        for c in range(n):
            if c == 0:
                ang = zig * math.radians(rng.uniform(8, 25))
                L = length * rng.uniform(0.68, 0.85)
            else:
                sgn = -zig if c == 1 else zig
                ang = sgn * math.radians(rng.uniform(35, 70))
                L = length * rng.uniform(0.55, 0.8)
            nd = _rot(d_end, (0, 0, 1), ang)
            nd.z += rng.uniform(-0.1, 0.1)
            if year + 1 == years:
                L = rng.uniform(0.03, 0.075)
            start = e - d_end * rng.uniform(0.0, 0.01)
            tip_guess = start + nd.normalized() * (L + 0.04)
            if year + 1 >= years - 1 and not self._free(tip_guess) and c > 0:
                continue  # почка не распустилась: на этом месте уже пучок
            self.shoot(start, nd, year + 1, years, L, r_end * rng.uniform(0.72, 0.9), -zig if c == 0 else zig,
                       branch_p)
        # короткие побеги этого года из боковых почек прошлогоднего (и позапрошлогоднего) прироста — с розетками
        n_lat = int(rng.choice([1, 1, 2, 2, 3])) if year == years - 1 else (int(rng.choice([0, 1, 1])) if year == years - 2 else 0)
        for _s in range(n_lat):
            t = rng.uniform(0.3, 0.8)
            sp = Vector(p).lerp(e, t)
            sd = _rot(d, (0, 0, 1), rng.choice([-1, 1]) * math.radians(rng.uniform(40, 80)))
            sd.z += rng.uniform(-0.1, 0.15)
            sd.normalize()
            se = sp + sd * rng.uniform(0.02, 0.05)
            if not self._free(se):
                continue
            self.tube(self.twig_new, sp, se, radius * 0.5, radius * 0.35, TWIG_NEW, sides=5)
            self.rosette(se, sd, n=int(rng.choice([4, 5, 5, 6])), scale=0.9)

    def objects(self, leaf_mat, twig_mat, name):
        objs = [self.leaves.to_object(name + '_leaves', [leaf_mat])]
        objs[0].data.polygons.foreach_set('use_smooth', [True] * len(objs[0].data.polygons))
        objs[0]['part'] = 'leaf'
        if self.twig_new.faces:
            o = self.twig_new.to_object(name + '_twnew', [twig_mat])
            o['part'] = 'twig'
            objs.append(o)
        if self.twig_old.faces:
            o = self.twig_old.to_object(name + '_twold', [twig_mat])
            o['part'] = 'frame'
            objs.append(o)
        for o in objs[1:]:
            o.data.polygons.foreach_set('use_smooth', [True] * len(o.data.polygons))
        return objs


def make_spray(kind, rs, sheet):
    sun, shade, mixed = [0, 1, 2, 3, 0, 1, 2, 3, 6, 7], [4, 5, 4, 5, 6], [0, 1, 4, 5, 2, 3, 6, 7]
    if kind in ('branchlet_sun', 'branchlet_mixed'):
        # ветвь 6-7 лет: короткий старый сук, из него 2-3 ветки по 5 (4) приростов веером — ~1.1-1.3 м, 50-80 розеток
        sun_k = kind == 'branchlet_sun'
        sp = Spray(rs, sheet, sun if sun_k else mixed, (0.078, 0.102) if sun_k else (0.088, 0.122),
                   min_gap=0.062 if sun_k else 0.085)
        bl = rs.uniform(0.12, 0.17)
        sp.tube(sp.twig_old, Vector((0, 0, 0)), Vector((rs.uniform(-0.01, 0.01), bl, 0)), 0.0095, 0.008, TWIG_OLD, sides=7)
        n = rs.choice([2, 3, 3]) if sun_k else 2
        angs = [-38, 4, 40] if n == 3 else [-26, 26]
        for k in range(n):
            a = math.radians(angs[k] + rs.uniform(-8, 8))
            d = Vector((-math.sin(a), math.cos(a), rs.uniform(-0.05, 0.08)))
            start = Vector((0, bl * (rs.uniform(0.55, 0.8) if k != n // 2 else 1.0), 0))
            if sun_k:
                sp.shoot(start, d, 0, 5, rs.uniform(0.12, 0.14), 0.0065, rs.choice([-1, 1]), (0.05, 0.5, 0.45))
            else:
                sp.shoot(start, d, 0, 4, rs.uniform(0.15, 0.17), 0.006, rs.choice([-1, 1]), (0.05, 0.55, 0.4))
        return sp
    if kind == 'large_sun':      # 5 годичных приростов, ~1-1.2 м, 30-50 розеток световых листьев 7.5-10 см
        sp = Spray(rs, sheet, sun, (0.078, 0.102), min_gap=0.062)
        sp.shoot(Vector((0, 0, 0)), Vector((0, 1, 0)), 0, 5, rs.uniform(0.12, 0.14), 0.006, rs.choice([-1, 1]),
                 (0.05, 0.5, 0.45))
    elif kind == 'large_mixed':  # теневые и смешанные листья 9-12.5 см, розетки по 4-6, ветка площе
        sp = Spray(rs, sheet, mixed, (0.09, 0.125), min_gap=0.09)
        sp.shoot(Vector((0, 0, 0)), Vector((0, 1, 0)), 0, 4, rs.uniform(0.14, 0.16), 0.0055, rs.choice([-1, 1]),
                 (0.05, 0.55, 0.4))
    elif kind == 'medium':       # ~0.6-0.7 м, 12-20 розеток
        sp = Spray(rs, sheet, sun, (0.075, 0.098), min_gap=0.07)
        sp.shoot(Vector((0, 0, 0)), Vector((0, 1, 0)), 0, 4, rs.uniform(0.085, 0.1), 0.0045, rs.choice([-1, 1]),
                 (0.05, 0.45, 0.5))
    else:                        # малый пучок ~0.35 м: 4-8 розеток
        sp = Spray(rs, sheet, sun, (0.075, 0.095), min_gap=0.065)
        sp.shoot(Vector((0, 0, 0)), Vector((0, 1, 0)), 0, 3, rs.uniform(0.055, 0.07), 0.0035, rs.choice([-1, 1]),
                 (0.0, 0.4, 0.6))
    return sp


# ------------------------------------------------------------------ материалы рендера атласа

def _pass_material(name, kind, images=None, smooth_const=0.12):
    """PASS 0 — альбедо (с мягким AO), 1 — нормаль в пространстве камеры (с bump у листьев; DOMEMIX > 0 — нормаль
    купола от точки DOME), 2 — маска (R гладкость, G AO, B 0.5). kind: 'leaf' (лист текстур, цвет вершин — оттенок
    листа) | 'vcol' (цвет вершин)."""
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, Lk = nt.nodes, nt.links
    N.clear()
    out = N.new('ShaderNodeOutputMaterial')
    em = N.new('ShaderNodeEmission')
    tr = N.new('ShaderNodeBsdfTransparent')
    mixs = N.new('ShaderNodeMixShader')
    vals = {}
    for k, v in (('PASS', 0.0), ('AOW', 0.25), ('AOD', 0.05), ('DOMEMIX', 0.0), ('DX', 0.0), ('DY', 0.0), ('DZ', 0.0),
                 ('AO2W', 0.0), ('AO2D', 0.6)):
        n = N.new('ShaderNodeValue')
        n.name = k
        n.outputs[0].default_value = v
        vals[k] = n.outputs[0]
    geo = N.new('ShaderNodeNewGeometry')
    normal = geo.outputs['Normal']
    if kind == 'leaf':
        uv = N.new('ShaderNodeUVMap')
        uv.uv_map = 'UVMap'
        ta = N.new('ShaderNodeTexImage')
        ta.image = images['albedo']
        ta.interpolation = 'Cubic'
        th = N.new('ShaderNodeTexImage')
        th.image = images['height']
        th.interpolation = 'Cubic'
        ts = N.new('ShaderNodeTexImage')
        ts.image = images['smooth']
        for t in (ta, th, ts):
            Lk.new(uv.outputs[0], t.inputs[0])
        bump = N.new('ShaderNodeBump')
        bump.inputs['Strength'].default_value = 0.25
        bump.inputs['Distance'].default_value = 0.001
        Lk.new(th.outputs['Color'], bump.inputs['Height'])
        normal = bump.outputs['Normal']
        tint = N.new('ShaderNodeAttribute')
        tint.attribute_name = 'Col'
        tm = N.new('ShaderNodeMix')
        tm.data_type = 'RGBA'
        tm.blend_type = 'MULTIPLY'
        tm.inputs['Factor'].default_value = 1.0
        Lk.new(ta.outputs['Color'], tm.inputs['A'])
        Lk.new(tint.outputs['Color'], tm.inputs['B'])
        col = tm.outputs['Result']
        smooth = ts.outputs['Color']
        Lk.new(ta.outputs['Alpha'], mixs.inputs['Fac'])
    else:
        vc = N.new('ShaderNodeAttribute')
        vc.attribute_name = 'Col'
        col = vc.outputs['Color']
        sv = N.new('ShaderNodeValue')
        sv.outputs[0].default_value = smooth_const
        smooth = sv.outputs[0]
        mixs.inputs['Fac'].default_value = 1.0
    # купол: нормаль от центра DOME (для «подкрон» LOD2)
    cxyz = N.new('ShaderNodeCombineXYZ')
    Lk.new(vals['DX'], cxyz.inputs[0])
    Lk.new(vals['DY'], cxyz.inputs[1])
    Lk.new(vals['DZ'], cxyz.inputs[2])
    sub = N.new('ShaderNodeVectorMath')
    sub.operation = 'SUBTRACT'
    Lk.new(geo.outputs['Position'], sub.inputs[0])
    Lk.new(cxyz.outputs[0], sub.inputs[1])
    nrm = N.new('ShaderNodeVectorMath')
    nrm.operation = 'NORMALIZE'
    Lk.new(sub.outputs[0], nrm.inputs[0])
    mixn = N.new('ShaderNodeMix')
    mixn.data_type = 'VECTOR'
    Lk.new(vals['DOMEMIX'], mixn.inputs['Factor'])
    Lk.new(normal, mixn.inputs[4])
    Lk.new(nrm.outputs[0], mixn.inputs[5])
    nrm2 = N.new('ShaderNodeVectorMath')
    nrm2.operation = 'NORMALIZE'
    Lk.new(mixn.outputs[1], nrm2.inputs[0])
    normal = nrm2.outputs[0]
    ao = N.new('ShaderNodeAmbientOcclusion')
    ao.samples = 16
    Lk.new(vals['AOD'], ao.inputs['Distance'])
    ao2 = N.new('ShaderNodeAmbientOcclusion')
    ao2.samples = 24
    Lk.new(vals['AO2D'], ao2.inputs['Distance'])

    def lerp1(aov, w):  # 1 - w + w*ao
        m1 = N.new('ShaderNodeMath')
        m1.operation = 'SUBTRACT'
        m1.inputs[0].default_value = 1.0
        Lk.new(w, m1.inputs[1])
        m2 = N.new('ShaderNodeMath')
        m2.operation = 'MULTIPLY_ADD'
        Lk.new(aov, m2.inputs[0])
        Lk.new(w, m2.inputs[1])
        Lk.new(m1.outputs[0], m2.inputs[2])
        return m2.outputs[0]

    k1 = lerp1(ao.outputs['AO'], vals['AOW'])
    k2 = lerp1(ao2.outputs['AO'], vals['AO2W'])
    kk = N.new('ShaderNodeMath')
    kk.operation = 'MULTIPLY'
    Lk.new(k1, kk.inputs[0])
    Lk.new(k2, kk.inputs[1])
    alb = N.new('ShaderNodeVectorMath')
    alb.operation = 'SCALE'
    Lk.new(col, alb.inputs[0])
    Lk.new(kk.outputs[0], alb.inputs['Scale'])
    vt = N.new('ShaderNodeVectorTransform')
    vt.vector_type = 'NORMAL'
    vt.convert_from = 'WORLD'
    vt.convert_to = 'CAMERA'
    enc = N.new('ShaderNodeVectorMath')
    enc.operation = 'MULTIPLY_ADD'
    enc.inputs[1].default_value = (0.5, 0.5, -0.5)
    enc.inputs[2].default_value = (0.5, 0.5, 0.5)
    Lk.new(normal, vt.inputs[0])
    Lk.new(vt.outputs[0], enc.inputs[0])
    comb = N.new('ShaderNodeCombineXYZ')
    sep = N.new('ShaderNodeSeparateColor')
    Lk.new(smooth, sep.inputs[0])
    Lk.new(sep.outputs[0], comb.inputs[0])
    aoall = N.new('ShaderNodeMath')
    aoall.operation = 'MULTIPLY'
    Lk.new(ao.outputs['AO'], aoall.inputs[0])
    Lk.new(k2, aoall.inputs[1])
    Lk.new(aoall.outputs[0], comb.inputs[1])
    comb.inputs[2].default_value = 0.5
    sel = []
    for target in (1.0, 2.0):
        c = N.new('ShaderNodeMath')
        c.operation = 'COMPARE'
        c.inputs[1].default_value = target
        c.inputs[2].default_value = 0.1
        Lk.new(vals['PASS'], c.inputs[0])
        sel.append(c.outputs[0])
    m1 = N.new('ShaderNodeMix')
    m1.data_type = 'RGBA'
    Lk.new(sel[0], m1.inputs['Factor'])
    Lk.new(alb.outputs[0], m1.inputs['A'])
    Lk.new(enc.outputs[0], m1.inputs['B'])
    m2 = N.new('ShaderNodeMix')
    m2.data_type = 'RGBA'
    Lk.new(sel[1], m2.inputs['Factor'])
    Lk.new(m1.outputs['Result'], m2.inputs['A'])
    Lk.new(comb.outputs[0], m2.inputs['B'])
    Lk.new(m2.outputs['Result'], em.inputs['Color'])
    Lk.new(tr.outputs[0], mixs.inputs[1])
    Lk.new(em.outputs[0], mixs.inputs[2])
    Lk.new(mixs.outputs[0], out.inputs['Surface'])
    return m


def set_values(**kw):
    for m in bpy.data.materials:
        if m.node_tree:
            for k, v in kw.items():
                if k in m.node_tree.nodes:
                    m.node_tree.nodes[k].outputs[0].default_value = float(v)


def _load_raw(path):
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.name = 'Non-Color'
    w, h = img.size
    px = np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1].copy()
    bpy.data.images.remove(img)
    return px


def _use_gpu(scene):
    """Cycles на видеокарте (OptiX/CUDA), если есть; иначе процессор."""
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        for t in ('OPTIX', 'CUDA'):
            try:
                prefs.compute_device_type = t
                prefs.get_devices()
                devs = [d for d in prefs.devices if d.type == t]
                if devs:
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


def _bbox(objs):
    pts = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
    return (min(p.x for p in pts), max(p.x for p in pts), min(p.y for p in pts), max(p.y for p in pts),
            min(p.z for p in pts), max(p.z for p in pts))


def _render_passes(scene, path_base, passes, view=None):
    """Рендер набора проходов: [(имя, PASS, srgb)] -> словарь массивов."""
    res = {}
    for nm, p, srgb in passes:
        set_values(PASS=p)
        scene.view_settings.view_transform = 'Standard' if srgb else 'Raw'
        path = f'{path_base}_{nm}.png'
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        res[nm] = eg.srgb_png_to_linear_values(path) if srgb else _load_raw(path)
    return res


def _cell_meta(alpha, size_m, attach, index, ci, cj, grid):
    """Рамка альфы, точка крепления и пятиугольник карточки (нижние углы срезаны) в UV ячейки (0..1, v вверх)."""
    h, w = alpha.shape
    on = alpha > 0.5
    rows = np.where(on.any(1))[0]
    cols = np.where(on.any(0))[0]
    pad = 2.0 / w
    v1 = min(1.0, 1 - rows.min() / h + pad)
    v0 = max(0.0, 1 - (rows.max() + 1) / h - pad)
    u0, u1 = max(0.0, cols.min() / w - pad), min(1.0, (cols.max() + 1) / w + pad)
    ua = float(np.clip(attach[0], u0 + 0.01, u1 - 0.01))
    va = float(v0)
    yy, xx = np.nonzero(alpha > 0.04)
    pu = (xx + 0.5) / w
    pv = 1 - (yy + 0.5) / h
    vl = vr = v1
    stalk = 0.03
    left = pu < ua - stalk
    if left.any():
        vl = float(np.min(va + (pv[left] - va) * (ua - u0) / np.maximum(ua - pu[left], 1e-4)))
    right = pu > ua + stalk
    if right.any():
        vr = float(np.min(va + (pv[right] - va) * (u1 - ua) / np.maximum(pu[right] - ua, 1e-4)))
    vl = float(np.clip(vl - pad, va, v1))
    vr = float(np.clip(vr - pad, va, v1))
    box = on[int((1 - v1) * h):int(math.ceil((1 - v0) * h)), int(u0 * w):int(math.ceil(u1 * w))]
    coverage = float(box.mean()) if box.size else 0.0
    gc, gr = grid
    uvr = [ci / gc, 1 - (cj + 1) / gr, (ci + 1) / gc, 1 - cj / gr]
    return dict(index=index, col=ci, row=cj, size=float(size_m), attach=[ua, float(np.clip(attach[1], v0, v1))],
                bottom=va, bbox=[u0, v0, u1, v1], left=vl, right=vr, coverage=coverage,
                uvRect=uvr, alphaBox=[uvr[0] + u0 / gc, uvr[1] + v0 / gr, uvr[0] + u1 / gc, uvr[1] + v1 / gr],
                worldSize=[float(size_m * (u1 - u0)), float(size_m * (v1 - v0))], anchor=[ua, va])


def oak_leaf_atlas(out_dir, seed=12, cell_px=1024, ss=2, samples=24, prefix='oak_leaves', clumps=True):
    """Атлас 2x2 (2048²) и атлас «подкрон» oak_clumps (1024², 2x2). Пишет PNG и *_cells.json."""
    eg.reset_scene()
    scene = bpy.context.scene
    sheet = leaf_sheet(seed=seed + 1)
    imgs = dict(albedo=_np_image('leaf_albedo', sheet['albedo'], False),
                height=_np_image('leaf_height', sheet['height'], True),
                smooth=_np_image('leaf_smooth', sheet['smooth'], True))
    leaf_mat = _pass_material('leaf_pass', 'leaf', imgs)
    twig_mat = _pass_material('twig_pass', 'vcol', smooth_const=0.12)
    tmp = os.path.join(out_dir, '_atlas_tmp')
    os.makedirs(tmp, exist_ok=True)
    # --- ветки атласа: из нескольких попыток — с покрытием 50-62 % и шириной около высоты
    kinds = ['branchlet_sun', 'branchlet_mixed', 'large_sun', 'medium']
    targets = {'branchlet_sun': (55, 95), 'branchlet_mixed': (24, 45), 'large_sun': (24, 40), 'medium': (10, 18)}
    groups = []
    for ci, kind in enumerate(kinds):
        best = None
        for attempt in range(12):
            rs = random.Random(seed * 100 + ci * 31 + attempt)
            sp = make_spray(kind, rs, sheet)
            xs = [v[0] for v in sp.leaves.verts] or [0]
            ys = [v[1] for v in sp.leaves.verts] or [0]
            w, h = max(xs) - min(xs), max(ys) - min(ys)
            lo, hi = targets[kind]
            nr = len(sp.rosettes)
            score = abs(math.log(max(w, 1e-3) / max(h, 1e-3))) * 1.5 + (0 if lo <= nr <= hi else 1 + abs(nr - (lo + hi) / 2) * 0.1)
            if best is None or score < best[0]:
                best = (score, sp, rs)
        groups.append(best[1].objects(leaf_mat, twig_mat, f'cell{ci}'))
        print('SPRAY', kind, 'rosettes', len(best[1].rosettes))
    eg.setup_card_render(scene, cell_px * ss, cell_px * ss, samples=samples)
    scene.cycles.transparent_max_bounces = 96
    scene.cycles.max_bounces = 4
    scene.render.filter_size = 1.0
    _use_gpu(scene)
    all_objs = [o for g in groups for o in g]
    N = 2 * cell_px
    al = np.zeros((N, N, 4), np.float32)
    nm = np.zeros((N, N, 3), np.float32)
    mk = np.zeros((N, N, 4), np.float32)
    cells = []
    set_values(AOW=0.22, AOD=0.05, DOMEMIX=0.0, AO2W=0.0)
    for k, objs in enumerate(groups):
        x0, x1, y0, y1, _, _ = _bbox(objs)
        size = max(x1 - x0, y1 - y0) * 1.04
        cx = (x0 + x1) * 0.5
        bottom = min(y0, 0.0) - size * 0.01
        cam = eg.ortho_camera(scene, (cx, bottom + size * 0.5, 20), (0, 0, 0), size, name=f'cam{k}')
        for o in all_objs:
            o.hide_render = o not in objs
        full = _render_passes(scene, os.path.join(tmp, f'{prefix}_{k}'), [('a', 0, True), ('n', 1, False), ('m', 2, False)])
        # каркас старых веточек отдельно — поверх листьев
        for o in all_objs:
            o.hide_render = not (o in objs and o.get('part') == 'frame')
        fw = _render_passes(scene, os.path.join(tmp, f'{prefix}_{k}_fw'), [('a', 0, True), ('n', 1, False)])
        bpy.data.objects.remove(cam)
        A = full['a'].copy()
        fa = fw['a'][..., 3:4] * 0.9
        A[..., :3] = A[..., :3] * (1 - fa) + fw['a'][..., :3] * fa
        A[..., 3] = np.maximum(A[..., 3], fw['a'][..., 3])
        Nn = full['n'][..., :3] * (1 - fa) + fw['n'][..., :3] * fa
        a_d = box_down(A, ss)
        n_d = box_down_plain(Nn * 2 - 1, ss)
        n_d /= np.maximum(np.linalg.norm(n_d, axis=-1, keepdims=True), 1e-6)
        m_d = box_down_plain(full['m'], ss)
        ci, cj = k % 2, k // 2
        sl = (slice(cj * cell_px, (cj + 1) * cell_px), slice(ci * cell_px, (ci + 1) * cell_px))
        al[sl] = a_d
        nm[sl] = n_d * 0.5 + 0.5
        mk[sl] = m_d
        cells.append(_cell_meta(a_d[..., 3], size, ((0 - (cx - size * 0.5)) / size, (0 - bottom) / size), k, ci, cj,
                                (2, 2)))
        cells[-1]['kind'] = kinds[k]
    a = al[..., 3]
    leaf = (a > 0.5)
    print('LEAF atlas mean', lin_to_srgb_hex(al[..., :3][leaf].mean(0)), 'coverage', [round(c['coverage'], 3) for c in cells])
    al[..., :3] = push_pull_fill(al[..., :3], a)
    nm = np.where(a[..., None] > 0.02, nm, np.array([0.5, 0.5, 1.0], np.float32))
    nm = push_pull_fill(nm, a)
    nm = nm * 2 - 1
    nm /= np.maximum(np.linalg.norm(nm, axis=-1, keepdims=True), 1e-6)
    nm = nm * 0.5 + 0.5
    mask = np.stack([np.zeros_like(a), np.where(a > 0.02, 0.35 + 0.65 * mk[..., 1], 1.0), np.full_like(a, 0.5),
                     np.where(a > 0.02, mk[..., 0], 0.3)], -1)
    names = _save_set(out_dir, prefix, al, nm, mask)
    with open(os.path.join(out_dir, prefix + '_cells.json'), 'w', encoding='utf-8') as f:
        json.dump(dict(grid=[2, 2], cells=cells), f, indent=1)
    if clumps:
        _clump_atlas(scene, out_dir, sheet, leaf_mat, twig_mat, all_objs, seed, tmp, leaf_mean=al[..., :3][leaf].mean(0))
    return names, cells


def _clump_atlas(scene, out_dir, sheet, leaf_mat, twig_mat, hide, seed, tmp, cell_px=512, ss=2, prefix='oak_clumps',
                 leaf_mean=None):
    """4 «подкроны» по 2-2.4 м: 10-14 веток из одной области веером наружу (купол к камере, +Z). Альбедо с AO
    (низ и середина темнее — самозатенение, которое Nature Renderer дальше 70 м тенями не даёт), нормаль купола."""
    for o in hide:
        o.hide_render = True
    al = np.zeros((2 * cell_px, 2 * cell_px, 4), np.float32)
    nm = np.zeros((2 * cell_px, 2 * cell_px, 3), np.float32)
    mk = np.zeros((2 * cell_px, 2 * cell_px, 4), np.float32)
    cells = []
    eg.setup_card_render(scene, cell_px * ss, cell_px * ss, samples=24)
    scene.cycles.transparent_max_bounces = 128
    _use_gpu(scene)
    for k in range(4):
        rs = random.Random(seed * 1000 + 77 * k)
        objs = []
        ns = rs.randint(18, 22)
        for i in range(ns):
            kind = 'large_sun' if i % 3 == 0 else ('large_mixed' if i % 3 == 1 else 'medium')
            sp = make_spray(kind, random.Random(seed * 50 + k * 17 + i), sheet)
            parts = sp.objects(leaf_mat, twig_mat, f'cl{k}_{i}')
            # ветка из глубины купола наружу: направление в конусе 0-75° от +Z, азимут веером
            az = i / ns * math.tau + rs.uniform(-0.3, 0.3)
            centre = i % 3 == 0  # каждая третья — из середины к камере: в центре подкроны нет «бублика»
            tilt = math.radians(rs.uniform(62, 85) if centre else rs.uniform(48, 82))
            out = Vector((math.cos(az) * math.sin(tilt), math.sin(az) * math.sin(tilt), math.cos(tilt)))
            base = (out * rs.uniform(0.0, 0.08) if centre else out * rs.uniform(0.15, 0.45)) + Vector((rs.uniform(-0.2, 0.2), rs.uniform(-0.2, 0.2), -0.2))
            # ориентация: ось ветки (+Y) — наружу, «верх» листьев (+Z) — к камере
            yax = out.normalized()
            zax = (Vector((0, 0, 1)) - yax * yax.z)
            zax = zax.normalized() if zax.length > 1e-3 else Vector((1, 0, 0))
            xax = yax.cross(zax).normalized()
            R = Matrix((xax, yax, zax)).transposed().to_4x4()
            s = rs.uniform(0.85, 1.1)
            M = Matrix.Translation(base) @ R @ Matrix.Rotation(rs.uniform(-0.3, 0.3), 4, 'Y') @ Matrix.Scale(s, 4)
            for o in parts:
                o.matrix_world = M
            objs += parts
        bpy.context.view_layer.update()
        x0, x1, y0, y1, z0, z1 = _bbox(objs)
        size = max(x1 - x0, y1 - y0) * 1.03
        cx, cy = (x0 + x1) * 0.5, (y0 + y1) * 0.5
        cam = eg.ortho_camera(scene, (cx, cy, 30), (0, 0, 0), size, name=f'clcam{k}')
        set_values(AOW=0.2, AOD=0.05, DOMEMIX=0.75, DX=cx, DY=cy, DZ=z0 - 0.4, AO2W=0.3, AO2D=0.7)
        res = _render_passes(scene, os.path.join(tmp, f'{prefix}_{k}'), [('a', 0, True), ('n', 1, False), ('m', 2, False)])
        bpy.data.objects.remove(cam)
        for o in objs:
            o.hide_render = True
        a_d = box_down(res['a'], ss)
        n_d = box_down_plain(res['n'][..., :3] * 2 - 1, ss)
        n_d /= np.maximum(np.linalg.norm(n_d, axis=-1, keepdims=True), 1e-6)
        m_d = box_down_plain(res['m'], ss)
        ci, cj = k % 2, k // 2
        sl = (slice(cj * cell_px, (cj + 1) * cell_px), slice(ci * cell_px, (ci + 1) * cell_px))
        al[sl] = a_d
        nm[sl] = n_d * 0.5 + 0.5
        mk[sl] = m_d
        meta = _cell_meta(a_d[..., 3], size, (0.5, 0.5), k, ci, cj, (2, 2))
        meta['kind'] = 'clump'
        meta['centre'] = [0.5, 0.5]
        cells.append(meta)
    set_values(DOMEMIX=0.0, AO2W=0.0)
    a = al[..., 3]
    if leaf_mean is not None:  # средний цвет как у листвы, чуть темнее (AO): LOD2 не темнеет против LOD1
        m = al[..., :3][a > 0.5].mean(0)
        al[..., :3] *= (np.asarray(leaf_mean) * 0.93 / np.maximum(m, 1e-5))[None, None, :]
    print('CLUMP mean', lin_to_srgb_hex(al[..., :3][a > 0.5].mean(0)), 'coverage', [round(c['coverage'], 3) for c in cells])
    al[..., :3] = push_pull_fill(al[..., :3], a)
    nm = np.where(a[..., None] > 0.02, nm, np.array([0.5, 0.5, 1.0], np.float32))
    nm = push_pull_fill(nm, a)
    nm = nm * 2 - 1
    nm /= np.maximum(np.linalg.norm(nm, axis=-1, keepdims=True), 1e-6)
    nm = nm * 0.5 + 0.5
    mask = np.stack([np.zeros_like(a), np.where(a > 0.02, 0.3 + 0.7 * mk[..., 1], 1.0), np.full_like(a, 0.5),
                     np.where(a > 0.02, mk[..., 0] * 0.8, 0.3)], -1)
    names = _save_set(out_dir, prefix, al, nm, mask)
    with open(os.path.join(out_dir, prefix + '_cells.json'), 'w', encoding='utf-8') as f:
        json.dump(dict(grid=[2, 2], cells=cells), f, indent=1)
    return names


def make_all(out_dir, which=('bark', 'branch', 'leaves')):
    os.makedirs(out_dir, exist_ok=True)
    if 'bark' in which:
        bark_trunk(out_dir)
    if 'branch' in which:
        bark_branch(out_dir)
    if 'leaves' in which:
        oak_leaf_atlas(out_dir)
    if 'clumps' in which:  # только атлас «подкрон» (средний цвет — по готовому атласу листвы)
        eg.reset_scene()
        scene = bpy.context.scene
        sheet = leaf_sheet(seed=13)
        imgs = dict(albedo=_np_image('leaf_albedo', sheet['albedo'], False),
                    height=_np_image('leaf_height', sheet['height'], True),
                    smooth=_np_image('leaf_smooth', sheet['smooth'], True))
        leaf_mat = _pass_material('leaf_pass', 'leaf', imgs)
        twig_mat = _pass_material('twig_pass', 'vcol', smooth_const=0.12)
        img = bpy.data.images.load(os.path.join(out_dir, 'oak_leaves_albedo.png'), check_existing=False)
        w, h = img.size
        px = np.array(img.pixels[:], np.float32).reshape(h, w, 4)
        bpy.data.images.remove(img)
        lin = np.where(px[..., :3] <= 0.04045, px[..., :3] / 12.92, ((px[..., :3] + 0.055) / 1.055) ** 2.4)
        tmp = os.path.join(out_dir, '_atlas_tmp')
        os.makedirs(tmp, exist_ok=True)
        _clump_atlas(scene, out_dir, sheet, leaf_mat, twig_mat, [], 12, tmp, leaf_mean=lin[px[..., 3] > 0.5].mean(0))
    if 'quick' in which:  # быстрый просмотр веток и подкрон: 256 px, без суперсэмплинга
        oak_leaf_atlas(os.path.join(out_dir, '_quick'), cell_px=320, ss=1, samples=6)


if __name__ == '__main__':
    import sys
    args = eg.script_args()
    make_all(args[0], tuple(args[1:]) or ('bark', 'branch', 'leaves'))
