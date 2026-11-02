# Кора сосны обыкновенной (Pinus sylvestris) для pine_gen.py: процедурная бесшовная кора двух видов (numpy).
#  - bark_lower: нижняя кора — высокие вертикальные пластины (4-15 x 10-30 см, вытянуты вдоль ствола 2-3:1) между глубокими
#    продольными бороздами 1-3 см; поперечные трещины узкие и мелкие (пластина ломается на отрезки), так что читается
#    продольная бороздчатость, а не «плитка». Пластина — стопка тонких чешуй: верхняя серо-бурая выветренная корка
#    отслаивается пятнами, под ней буро-красные слои, местами свежий рыжий скол. Стенки борозд слоистые (тонкие
#    поперечные полоски слоёв), красно-бурые, дно тёмно-бурое (не чёрное). 1024 x 2048 на 0.6 м по окружности x 1.2 м.
#    Одна пластина на плитке — серебристо-серая выветренная (вертикальные волокна): её внутренний прямоугольник
#    (deadwood_rect в pine_bark_lower.json) — UV сухих ветвей и обломков сучьев (серое мёртвое дерево без третьего
#    материала).
#  - bark_upper: «лисья» кора — атласная оранжевая кожица с тонкими поперечными штрихами; по ней тонкие бумажные
#    чешуйки 1-5 см (шире, чем высота), светлее кожицы, прирастают верхним краем, нижний край отстаёт, загибается
#    и светлеет (#DB9A63), под ним узкая тень (#9A522E); пятна сероватой плёнки старой коры и водорослей/лишайника.
#    1024 x 1024 на 0.5 x 0.5 м.
# Маски — соглашение NatureManufacture: R металличность 0, G AO, B высота, A гладкость. Нормали — OpenGL (+Y вверх).
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402


# ------------------------------------------------------------------ бесшовные шумы (numpy)

def _fade(t):
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


class PNoise:
    """Периодический градиентный шум на решётке px x py (координаты — в клетках решётки)."""

    def __init__(self, px, py, seed):
        rng = np.random.default_rng(seed)
        a = rng.random((py, px)) * math.tau
        self.gx = np.cos(a).astype(np.float32)
        self.gy = np.sin(a).astype(np.float32)
        self.px, self.py = px, py

    def __call__(self, x, y):
        xi = np.floor(x).astype(np.int64)
        yi = np.floor(y).astype(np.int64)
        fx = (x - xi).astype(np.float32)
        fy = (y - yi).astype(np.float32)
        x0, x1 = xi % self.px, (xi + 1) % self.px
        y0, y1 = yi % self.py, (yi + 1) % self.py
        gx, gy = self.gx, self.gy
        n00 = gx[y0, x0] * fx + gy[y0, x0] * fy
        n10 = gx[y0, x1] * (fx - 1) + gy[y0, x1] * fy
        n01 = gx[y1, x0] * fx + gy[y1, x0] * (fy - 1)
        n11 = gx[y1, x1] * (fx - 1) + gy[y1, x1] * (fy - 1)
        u, v = _fade(fx), _fade(fy)
        a = n00 + (n10 - n00) * u
        b = n01 + (n11 - n01) * u
        return (a + (b - a) * v) * 1.41


def fbm(X, Y, W, H, px, py, seed, octaves=4, persistence=0.5):
    """Фрактальный шум в пикселях X, Y текстуры W x H (бесшовно): базовый период px x py клеток, примерно -1..1."""
    out = np.zeros(np.shape(X), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        n = PNoise(px << o, py << o, seed + o * 101)
        out += amp * n(X / W * (px << o), Y / H * (py << o))
        total += amp
        amp *= persistence
    return out / total


def voronoi_aniso(X, Y, W, H, nx, ny, seed, sx=1.0, sy=1.0, jitter=1.0):
    """Бесшовный Вороной в растянутой метрике: расстояния считаются в пространстве (X/sx, Y/sy) — клетки вытянуты в sx/sy
    раз, но расстояние до границы клетки возвращается в НАСТОЯЩИХ пикселях (по биссектрисе, переведённой обратно), вместе
    с единичной нормалью ближайшей границы (в настоящих пикселях), случайным числом клетки и её индексом.
    Решётка nx x ny клеток на всю текстуру (W/nx x H/ny пикселей на клетку до растяжения)."""
    rng = np.random.default_rng(seed)
    jit = rng.random((ny, nx, 2)).astype(np.float32)
    ids = rng.random((ny, nx)).astype(np.float32)
    cw, ch = W / nx, H / ny
    cx = np.floor(X / cw).astype(np.int64)
    cy = np.floor(Y / ch).astype(np.int64)
    # окно поиска соседей: в растянутом пространстве клетка (cw/sx) x (ch/sy)
    rx = int(math.ceil(max(1.0, (ch / sy) / (cw / sx)))) + 1
    ry = int(math.ceil(max(1.0, (cw / sx) / (ch / sy)))) + 1

    def point(ix, iy):
        jx = jit[iy % ny, ix % nx, 0]
        jy = jit[iy % ny, ix % nx, 1]
        return (ix + 0.5 + (jx - 0.5) * jitter) * cw, (iy + 0.5 + (jy - 0.5) * jitter) * ch

    d1 = np.full(X.shape, np.inf, np.float32)
    bx = np.zeros(X.shape, np.int64)
    by = np.zeros(X.shape, np.int64)
    p1x = np.zeros(X.shape, np.float32)
    p1y = np.zeros(X.shape, np.float32)
    for oy in range(-ry, ry + 1):
        for ox in range(-rx, rx + 1):
            ix, iy = cx + ox, cy + oy
            px, py = point(ix, iy)
            d = ((X - px) / sx) ** 2 + ((Y - py) / sy) ** 2
            c = d < d1
            d1 = np.where(c, d, d1)
            bx = np.where(c, ix, bx)
            by = np.where(c, iy, by)
            p1x = np.where(c, px, p1x)
            p1y = np.where(c, py, p1y)
    edge = np.full(X.shape, -np.inf, np.float32)
    enx = np.zeros(X.shape, np.float32)
    eny = np.zeros(X.shape, np.float32)
    for oy in range(-ry - 1, ry + 2):
        for ox in range(-rx - 1, rx + 2):
            if ox == 0 and oy == 0:
                continue
            ix, iy = bx + ox, by + oy
            px, py = point(ix, iy)
            # биссектриса в растянутом пространстве: n_s . (x_s - m_s) = 0, n_s = p2_s - p1_s;
            # в настоящих пикселях нормаль n_r = (n_sx / sx, n_sy / sy)
            nrx = (px - p1x) / (sx * sx)
            nry = (py - p1y) / (sy * sy)
            ln = np.sqrt(nrx * nrx + nry * nry) + 1e-9
            nrx, nry = nrx / ln, nry / ln
            e = (X - (p1x + px) * 0.5) * nrx + (Y - (p1y + py) * 0.5) * nry
            c = e > edge
            edge = np.where(c, e, edge)
            enx = np.where(c, nrx, enx)
            eny = np.where(c, nry, eny)
    cid = ids[by % ny, bx % nx]
    cidx = (by % ny) * nx + (bx % nx)
    return np.maximum(-edge, 0.0), enx, eny, cid, cidx


def gauss_blur(a, sigma_x, sigma_y=None):
    """Бесшовное (периодическое) гауссово размытие через FFT."""
    sigma_y = sigma_x if sigma_y is None else sigma_y
    h, w = a.shape
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.rfftfreq(w)[None, :]
    g = np.exp(-2 * (math.pi ** 2) * ((fx * sigma_x) ** 2 + (fy * sigma_y) ** 2))
    return np.fft.irfft2(np.fft.rfft2(a) * g, s=a.shape).astype(np.float32)


def height_to_normal(height, strength):
    """Бесшовная нормаль (OpenGL: +Y вверх по картинке) из высоты; strength — перевод высоты (0..1) в пиксели."""
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * 0.5 * strength
    dy = (np.roll(height, 1, 0) - np.roll(height, -1, 0)) * 0.5 * strength
    n = np.stack([-dx, -dy, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return (n * 0.5 + 0.5).astype(np.float32)


def cavity_ao(height, radii=(4, 16, 48), weights=(0.5, 0.8, 0.9), floor=0.25, k=2.2):
    """AO по высоте: насколько точка ниже окрестности на нескольких масштабах."""
    occ = np.zeros_like(height)
    for r, w in zip(radii, weights):
        occ += np.maximum(gauss_blur(height, r) - height, 0) * w
    return np.clip(1.0 - occ * k, floor, 1.0).astype(np.float32)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def hexc(h):
    return np.array(eg.hex_rgb(h), np.float32)


def mix(a, b, t):
    t = np.asarray(t, np.float32)
    if t.ndim:
        t = t[..., None]
    return a + (b - a) * t


def srgb_mean(col):
    """Средний цвет (линейные значения) -> sRGB hex."""
    m = np.clip(col.reshape(-1, 3).mean(0), 0, 1)
    s = np.where(m <= 0.0031308, m * 12.92, 1.055 * m ** (1 / 2.4) - 0.055)
    return '#%02x%02x%02x' % tuple(int(round(v * 255)) for v in s)


def lum(c):
    return 0.2126 * c[..., 0] + 0.7152 * c[..., 1] + 0.0722 * c[..., 2]


def save_bark(out_dir, prefix, albedo, height, normal, ao, smooth):
    mask = np.stack([np.zeros_like(height), ao, height, smooth], -1)
    names = [prefix + '_albedo.png', prefix + '_normal.png', prefix + '_mask.png']
    eg.save_png(np.clip(albedo, 0, 1), os.path.join(out_dir, names[0]))
    eg.save_png(normal, os.path.join(out_dir, names[1]), linear=True)
    eg.save_png(np.clip(mask, 0, 1), os.path.join(out_dir, names[2]), linear=True)
    return names


def lit_preview(albedo, normal, ao, path, light=(-0.55, 0.45, 0.70), crop=None):
    """Проверочная картинка: альбедо, освещённое скользящим светом по карте нормалей (без Cycles)."""
    n = normal * 2 - 1
    l = np.array(light, np.float32)
    l /= np.linalg.norm(l)
    d = np.clip((n * l).sum(-1), 0, 1)
    img = albedo * (0.25 + 0.9 * d)[..., None] * (0.6 + 0.4 * ao)[..., None]
    if crop is not None:
        img = img[crop[0]:crop[1], crop[2]:crop[3]]
    eg.save_png(np.clip(img, 0, 1), path)


def largest_rect(ok, cy, cx, max_aspect=6.0):
    """Наибольший прямоугольник (по площади, высота/ширина <= max_aspect) с центром (cy, cx), весь внутри ok."""
    S = np.pad(np.cumsum(np.cumsum(ok.astype(np.int64), 0), 1), ((1, 0), (1, 0)))

    def full(y0, y1, x0, x1):
        if y0 < 0 or x0 < 0 or y1 > ok.shape[0] or x1 > ok.shape[1]:
            return False
        return S[y1, x1] - S[y0, x1] - S[y1, x0] + S[y0, x0] == (y1 - y0) * (x1 - x0)

    best = None
    for a in range(6, 200, 2):
        if not full(cy - 2, cy + 2, cx - a, cx + a):
            break
        lo, hi = 2, 2000
        while lo < hi:
            b = (lo + hi + 1) // 2
            if full(cy - b, cy + b, cx - a, cx + a):
                lo = b
            else:
                hi = b - 1
        b = min(lo, int(a * max_aspect))
        area = a * b
        if best is None or area > best[0]:
            best = (area, cy - b, cy + b, cx - a, cx + a)
    return best


# ------------------------------------------------------------------ кора: низ ствола

def bark_lower(out_dir, W=1024, H=2048, seed=11, prefix='pine_bark_lower', tile=(0.6, 1.2), preview_dir=None):
    """Нижняя кора: tile[0] м (W) x tile[1] м (H). Размеры в пикселях ниже — для 1024 x 2048 (0.586 мм/пикс)."""
    k = W / 1024.0
    mm = tile[0] / W * 1000.0   # мм на пиксель
    Y, X = np.mgrid[0:H, 0:W].astype(np.float32)
    # искривление: борозды волнистые, сходятся и расходятся
    wx = fbm(X, Y, W, H, 3, 4, seed + 1, 4) * 34 * k + fbm(X, Y, W, H, 8, 14, seed + 2, 3) * 9 * k
    wy = fbm(X, Y, W, H, 3, 3, seed + 3, 4) * 26 * k
    XW, YW = X + wx, Y + wy
    # основные пластины: ~10 x 25 см, вытянуты вдоль ствола
    eA, nAx, nAy, idA, ixA = voronoi_aniso(XW, YW, W, H, 6, 5, seed + 5, sx=1.0, sy=2.6)
    # деление части пластин продольной/косой трещиной помельче: ~5 x 12 см
    eB, nBx, nBy, idB, _ = voronoi_aniso(XW + wx * 0.4, YW, W, H, 12, 10, seed + 6, sx=1.0, sy=2.3)
    regB = smoothstep(0.05, 0.35, fbm(X, Y, W, H, 3, 4, seed + 7, 3) + 0.25 * (idA - 0.5))
    jag = fbm(X, Y, W, H, 26, 50, seed + 8, 3) * 4.5 * k + fbm(X, Y, W, H, 90, 160, seed + 9, 2) * 1.6 * k
    wnoise = 0.5 + 0.5 * fbm(X, Y, W, H, 4, 10, seed + 10, 3)
    vertA = np.abs(nAx) ** 1.6   # 1 — вертикальная граница (продольная борозда), 0 — поперечная трещина
    vertB = np.abs(nBx) ** 1.6
    # полуширина: продольные борозды 1.3-2.6 см, поперечные 0.3-0.7 см; глубина у поперечных меньше
    hwA = (7.0 + 21.0 * vertA) * (0.7 + 0.6 * wnoise) * k
    hwB = (3.0 + 8.0 * vertB) * (0.6 + 0.6 * wnoise) * k
    dA = 0.55 + 0.45 * vertA
    dB = (0.25 + 0.3 * vertB) * regB
    eAj = eA + jag
    eBj = eB + jag * 0.8
    tA = np.clip(eAj / hwA, 0, 1)
    tB = np.clip(eBj / hwB, 0, 1)
    fis = np.maximum(dA * (1 - tA) ** 0.85, dB * (1 - tB) ** 1.1)   # глубина борозды 0..1
    wall = np.maximum((1 - tA) * (tA > 0.02) * dA, (1 - tB) * dB * 0.8)   # склон борозды (для слоистых стенок)
    # выпуклость пластины (плечи скруглены)
    plate_d = np.minimum(eAj, np.where(regB > 0.3, eBj, 1e4))
    bulge = smoothstep(0, 28 * k, plate_d) * 0.6 + smoothstep(0, 90 * k, plate_d) * 0.4
    # чешуи пластины: 3 слоя отслаиваются независимыми пятнами (рваные края, ступенька с кромкой)
    lay_removed = np.zeros((H, W), np.float32)
    lip = np.zeros((H, W), np.float32)
    for i, (cx_, cy_, t) in enumerate(((9, 11, 0.04), (15, 17, 0.3), (24, 26, 0.55))):
        n = (fbm(X + wx * 0.5, Y, W, H, cx_, cy_, seed + 20 + i, 5, 0.6)
             + fbm(X, Y, W, H, cx_ * 6, cy_ * 5, seed + 30 + i, 2) * 0.22)
        rem = smoothstep(t - 0.01, t + 0.01, n) * (lay_removed >= i - 0.5)
        lip = np.maximum(lip, np.exp(-np.abs(n - t) / 0.015) * (lay_removed >= i - 0.5))
        lay_removed += rem
    # тонкие чешуи — стопка мелких пластинок ~1.5-3 x 3-6 см со ступеньками между ними и трещинками
    eF, _, _, idF, _ = voronoi_aniso(XW + jag * 2.0, YW, W, H, 34, 30, seed + 40, sx=1.0, sy=1.8)
    eG, _, _, idG, _ = voronoi_aniso(XW + jag * 1.5, YW + wx, W, H, 70, 52, seed + 44, sx=1.0, sy=1.5)
    crack = (1 - smoothstep(0.0, 1.8 * k, eF + jag * 0.3)) * (idF > 0.2)
    crack2 = (1 - smoothstep(0.0, 1.2 * k, eG + jag * 0.2)) * (idG > 0.45)
    grain = fbm(X, Y, W, H, 60, 30, seed + 41, 3)
    rough = fbm(X, Y, W, H, 30, 40, seed + 45, 4, 0.6)
    stria_v = fbm(X, Y, W, H, 90, 8, seed + 42, 2)    # продольные волокна
    # слои стенок: неровные поперечные полоски разной толщины
    lam_ph = Y * (math.tau / (5.5 * k)) * (1 + 0.35 * fbm(X, Y, W, H, 6, 24, seed + 46, 2)) + fbm(X, Y, W, H, 10, 40, seed + 43, 3) * 9.0
    lam = (0.5 + 0.5 * np.sin(lam_ph)) * (0.5 + 0.5 * smoothstep(-0.3, 0.4, fbm(X, Y, W, H, 12, 60, seed + 47, 2)))
    # высота 0..1
    flaky = fbm(X, Y, W, H, 120, 150, seed + 48, 2)
    surf = (0.78 + 0.10 * bulge - 0.085 * lay_removed + 0.02 * lip - 0.04 * crack - 0.025 * crack2
            + 0.03 * (idF - 0.5) + 0.015 * (idG - 0.5) + 0.04 * rough + 0.015 * flaky + 0.012 * grain
            + 0.012 * stria_v)
    h = surf * (1.0 - fis)
    h = h - wall * 0.04 * lam
    # серебристо-серая выветренная пластина: клетка A с наибольшим внутренним расстоянием вдали от краёв плитки
    ok_mask = eAj > hwA + 10 * k
    interior = np.where((Y > 260 * k) & (Y < H - 260 * k) & (X > 90 * k) & (X < W - 90 * k), eAj - hwA, -1)
    cy, cx = np.unravel_index(int(np.argmax(interior)), interior.shape)
    grey_cell = ixA == ixA[cy, cx]
    rect = largest_rect(ok_mask & grey_cell, int(cy), int(cx))
    grey = grey_cell.astype(np.float32) * smoothstep(0, 6 * k, eAj - hwA * 0.5)
    h_grey = (0.80 + 0.10 * bulge + 0.02 * stria_v + 0.03 * rough + 0.012 * flaky - 0.03 * crack - 0.02 * crack2
              + 0.02 * (idF - 0.5)) * (1 - fis)
    h = np.where(grey > 0.5, h_grey, h)
    h = np.clip(h, 0, 1).astype(np.float32)

    # ---- цвет
    c_crust = hexc('#7d7166')     # серая выветренная корка
    c_main = hexc('#665648')      # серо-бурая поверхность
    c_l1 = hexc('#6a4733')        # первый слой под коркой — буроватый
    c_l2 = hexc('#7a462f')        # красно-бурый слой
    c_l3 = hexc('#8d5638')        # свежий рыжий скол
    c_wall_hi = hexc('#6b3f2a')
    c_wall_lo = hexc('#4a2c1e')
    c_deep = hexc('#2a201a')
    weather = smoothstep(-0.1, 0.35, fbm(X, Y, W, H, 6, 9, seed + 50, 4) + 0.3 * (idA - 0.5))
    top = mix(c_main, c_crust, weather * 0.85)
    top = top * (0.92 + 0.16 * idA[..., None])
    col = top
    col = mix(col, c_l1 * (0.95 + 0.1 * idA[..., None]), np.clip(lay_removed, 0, 1))
    col = mix(col, c_l2, np.clip(lay_removed - 1, 0, 1))
    col = mix(col, c_l3, np.clip(lay_removed - 2, 0, 1))
    col = mix(col, col * 1.12, lip * 0.6 * (lay_removed < 2.5))      # светлая кромка чешуи
    col = col * (1.0 - 0.18 * crack - 0.10 * crack2)[..., None]
    col = col * (0.95 + 0.08 * (idF - 0.5) + 0.06 * rough)[..., None]
    col = col * (0.95 + 0.06 * grain)[..., None]
    # стенки борозд: слоистые красно-бурые, к дну темнее
    walls = mix(c_wall_lo, c_wall_hi, lam * 0.5 + 0.25)
    walls = mix(walls, col, smoothstep(0.55, 0.95, 1 - fis / np.maximum(dA, 0.3)))
    walls = mix(c_deep, walls, smoothstep(0.0, 0.45, 1 - fis / np.maximum(dA, 0.3)) ** 0.8)
    col = mix(walls, col, smoothstep(0.82, 0.97, 1 - fis))
    # лишайник на гребнях
    lich = smoothstep(0.42, 0.62, fbm(X, Y, W, H, 5, 7, seed + 60, 4)) * smoothstep(0.85, 0.95, h) * (1 - grey)
    col = mix(col, hexc('#8e9478'), lich * 0.35)
    # серая пластина: серебристо-серое дерево/корка с продольными волокнами
    c_grey = hexc('#7f786d') * (0.92 + 0.12 * (0.5 + 0.5 * stria_v))[..., None] * (0.90 + 0.16 * rough)[..., None]
    c_grey = c_grey * (1.0 - 0.18 * crack - 0.1 * crack2)[..., None] * (0.95 + 0.08 * (idF - 0.5))[..., None]
    c_grey = mix(c_grey, hexc('#6e675e'), smoothstep(0.3, 0.9, -stria_v) * 0.35)
    col = mix(col, c_grey, grey * smoothstep(0.85, 0.97, 1 - fis))
    ao = cavity_ao(h, radii=(3 * k, 12 * k, 40 * k), weights=(0.5, 0.8, 0.8), floor=0.3, k=2.0)
    albedo = col * (0.70 + 0.30 * ao[..., None])
    # средняя яркость — как у серо-бурой коры (#6F5F52 по справке), оттенок свой
    target = lum(hexc('#6f5f52')[None, None, :])[0, 0]
    albedo = albedo * (target / max(float(lum(albedo).mean()), 1e-4))
    normal = height_to_normal(h, 40.0 / mm * 0.55)    # рельеф ~4 см
    smooth = (0.07 + 0.08 * h + 0.03 * lip - 0.03 * fis + 0.03 * grey).astype(np.float32)
    names = save_bark(out_dir, prefix, albedo, h, normal, ao, np.clip(smooth, 0.04, 0.2))
    # прямоугольник серой пластины в UV (v снизу вверх)
    _, y0, y1, x0, x1 = rect
    info = dict(tile=list(tile), size=[W, H], mean_srgb=srgb_mean(albedo),
                deadwood_rect=[x0 / W, 1 - y1 / H, x1 / W, 1 - y0 / H],
                deadwood_size_m=[(x1 - x0) / W * tile[0], (y1 - y0) / H * tile[1]])
    with open(os.path.join(out_dir, prefix + '.json'), 'w') as f:
        json.dump(info, f, indent=1)
    if preview_dir:
        lit_preview(albedo, normal, ao, os.path.join(preview_dir, prefix + '_lit.png'))
    print('BARK_LOWER', json.dumps(info))
    return names, info


# ------------------------------------------------------------------ кора: «лисья» верхняя

def bark_upper(out_dir, S=1024, seed=21, prefix='pine_bark_upper', tile=0.5, preview_dir=None):
    """Верх ствола и толстые ветви: tile x tile м (0.49 мм/пикс при 1024 на 0.5 м), рельеф ~3 мм."""
    W = H = S
    k = S / 1024.0
    mm = tile / S * 1000.0
    Y, X = np.mgrid[0:H, 0:W].astype(np.float32)
    wx = fbm(X, Y, W, H, 4, 4, seed + 1, 4) * 22 * k + fbm(X, Y, W, H, 16, 16, seed + 2, 3) * 7 * k
    wy = fbm(X, Y, W, H, 4, 4, seed + 3, 4) * 12 * k + fbm(X, Y, W, H, 16, 16, seed + 4, 3) * 4 * k
    fine = fbm(X, Y, W, H, 64, 64, seed + 5, 3)
    hgrain = fbm(X, Y, W, H, 6, 160, seed + 6, 3)       # тонкие поперечные штрихи кожицы
    dash = smoothstep(0.35, 0.6, fbm(X, Y, W, H, 24, 220, seed + 7, 2))   # короткие поперечные чёрточки
    big = 0.5 + 0.5 * fbm(X, Y, W, H, 2, 3, seed + 8, 4)
    c_skin = hexc('#c0703f')
    c_skin_r = hexc('#b8602f')
    c_skin_l = hexc('#c97d4a')
    skin = mix(c_skin_r, c_skin_l, smoothstep(0.25, 0.8, big))
    skin = skin * (0.96 + 0.06 * fine)[..., None] * (0.97 + 0.05 * hgrain)[..., None]
    skin = mix(skin, hexc('#9a522e'), dash * 0.35)
    col = skin.copy()
    hgt = (0.10 + 0.03 * fine + 0.015 * hgrain).astype(np.float32)
    smooth = np.full((H, W), 0.38, np.float32)
    flake_any = np.zeros((H, W), np.float32)
    rim_all = np.zeros((H, W), np.float32)
    shadow = np.zeros((H, W), np.float32)
    specs = [  # клеток X, Y; растяжение по X; доля чешуек; базовая высота; цвет чешуйки
        (10, 22, 2.3, 0.42, 0.42, '#c98252'),
        (18, 36, 2.1, 0.30, 0.60, '#cd8957'),
        (32, 60, 1.9, 0.16, 0.76, '#d2915e'),
    ]
    for i, (nx, ny, sx, share, base, cc) in enumerate(specs):
        ww = fbm(X, Y, W, H, 22, 22, seed + 60 + i, 3) * 9 * k
        e, enx, eny, cid, cidx = voronoi_aniso(X + wx * (1 + 0.4 * i) + ww, Y + wy + ww * 0.5, W, H, nx, ny,
                                               seed + 10 + i, sx=sx, sy=1.0)
        present = (cid < share).astype(np.float32)
        er = e + fbm(X, Y, W, H, 50, 50, seed + 20 + i, 3) * 4.0 * k
        gap = (1.5 + 2.0 * ((cid * 7.3) % 1.0)) * k
        cover = smoothstep(gap, gap + 1.2 * k, er) * present
        # свободный край — нижний (и немного боковые): относительное положение пикселя в клетке по Y
        cell_h = H / ny
        # расстояние вниз до края клетки ~ (er при нормали, смотрящей вниз)
        down = np.clip(eny, 0, 1)   # нормаль ближайшей границы смотрит вниз по картинке (Y растёт вниз)
        free = smoothstep(0.25, 0.85, down + 0.25 * np.abs(enx))
        curl = np.exp(-np.maximum(er - gap, 0) / (4.0 * k)) * cover * free
        mott = (0.95 + 0.10 * ((cid * 13.1) % 1.0))[..., None]
        film = ((cid * 3.7) % 1.0 < 0.25).astype(np.float32)
        fc = mix(hexc(cc), hexc('#b07a5a'), film * 0.6) * mott
        fc = fc * (0.97 + 0.05 * fine)[..., None]
        fc = mix(fc, hexc('#db9a63'), curl * 0.8)
        lay = base + 0.10 * curl + 0.02 * fine
        newly = cover * (lay > hgt)
        hgt = np.where(newly > 0.5, lay, hgt)
        col = mix(col, fc, newly)
        smooth = np.where(newly > 0.5, 0.26 + 0.06 * curl, smooth)
        # тень под отставшей кромкой: пиксели ниже чешуйки (сдвиг маски вниз по картинке)
        edge_mask = cover * free
        sh = np.zeros_like(edge_mask)
        for d in range(1, int(6 * k) + 1):
            sh = np.maximum(sh, np.roll(edge_mask, d, axis=0) * (1 - d / (7 * k)))
        shadow = np.maximum(shadow * (1 - newly), sh * (1 - cover))
        flake_any = np.maximum(flake_any, cover)
        rim_all = np.maximum(rim_all * (1 - newly), curl)
    col = mix(col, hexc('#9a522e'), np.clip(shadow, 0, 1) * 0.55)
    # пятна сероватой плёнки старой коры
    filmp = smoothstep(0.15, 0.45, fbm(X, Y, W, H, 3, 4, seed + 30, 4))
    col = mix(col, hexc('#b07a5a') * (0.95 + 0.1 * fine)[..., None], filmp * 0.35)
    smooth = smooth - 0.08 * filmp
    # водоросли/лишайник 5-8 %
    alg = smoothstep(0.50, 0.70, fbm(X, Y, W, H, 4, 5, seed + 31, 4) + 0.15 * fine)
    col = mix(col, hexc('#8e9478'), alg * 0.45)
    smooth = smooth - 0.1 * alg
    h = hgt + 0.01 * hgrain
    h = np.clip((h - h.min()) / (np.percentile(h, 99.8) - h.min()), 0, 1).astype(np.float32)
    ao = cavity_ao(h, radii=(2 * k, 8 * k, 26 * k), weights=(0.6, 0.5, 0.3), floor=0.55)
    albedo = col * (0.85 + 0.15 * ao[..., None])
    target = lum(hexc('#b8703f')[None, None, :])[0, 0]
    albedo = albedo * (target / max(float(lum(albedo).mean()), 1e-4))
    normal = height_to_normal(h, 3.0 / mm * 0.9)
    names = save_bark(out_dir, prefix, albedo, h, normal, ao, np.clip(smooth, 0.12, 0.45))
    info = dict(tile=[tile, tile], size=[S, S], mean_srgb=srgb_mean(albedo),
                flake_cover=float(flake_any.mean()), lichen=float((alg > 0.5).mean()))
    with open(os.path.join(out_dir, prefix + '.json'), 'w') as f:
        json.dump(info, f, indent=1)
    if preview_dir:
        lit_preview(albedo, normal, ao, os.path.join(preview_dir, prefix + '_lit.png'), light=(-0.3, 0.6, 0.75))
    print('BARK_UPPER', json.dumps(info))
    return names, info


if __name__ == '__main__':
    import sys
    args = eg.script_args()
    out = args[0]
    prev = args[1] if len(args) > 1 else None
    which = args[2:] or ['lower', 'upper']
    os.makedirs(out, exist_ok=True)
    if prev:
        os.makedirs(prev, exist_ok=True)
    import time
    t = time.time()
    if 'lower' in which:
        bark_lower(out, preview_dir=prev)
        print('lower', time.time() - t)
    t = time.time()
    if 'upper' in which:
        bark_upper(out, preview_dir=prev)
        print('upper', time.time() - t)
