"""Trench wall textures for GeoWorld (GeoTrenchMesh's walls): one atlas of five strips, each 4 m along a wall (tiling) and the
wall's height up it (0 at the foot, 1 at the top edge):

  0  bare cut soil: turf and root mat at the top, dark chernozem with fine roots, then yellow-brown loess loam with the
     marks of the spade or the excavator's bucket, a few pebbles and krotovinas, loose wet soil slumped at the foot
  1  the same, another stretch: wetter, more slumped, more stones
  2  boards laid flat behind round posts (the common revetment), weathered, some broken or gone, soil above them
  3  wattle: rods woven round stakes, grey bark, soil above
  4  round timber for a dugout's roof: 4 m along a log, once round it (0 its underside, 0.5 its top), weathered grey bark,
     soil from the cover left on its top and mud on its underside

Built from photo scans already in the package (Poly Haven, CC0: brown_mud_03, dry_mud_field_001, grass_ground in
Environment/Textures/Ground/PolyHaven; jolcham_oak_bark_01 as the greyed oak_dead_bark of Environment/Vegetation/Generated/Oak):
their pattern (brightness against their own mean) under the colours of the soil horizons, the rest (roots, tool marks, boards,
rods) drawn here. Writes Textures/Trenches/trench_walls_albedo.png (sRGB), _normal.png (OpenGL, as Unity), _mask.png (R metallic
0, G occlusion, A smoothness: URP Lit's metallic-gloss and occlusion maps both). The image is 2048 x 2560 (five strips of 512);
Unity scales it to the nearest power of two (2048 square), the strips' share of it stays.

    py -I Tools~/Trenches/trench_textures.py <package folder>
"""
import os
import sys

import numpy as np
from PIL import Image

W, H = 2048, 512          # a strip: 4 m along x 1.3 m up
STRIPS = 5
PX = W / 4.0              # pixels a metre along
PY = H / 1.3              # pixels a metre up


def lin(c):
    c = np.asarray(c, np.float32) / 255.0
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def srgb(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def load_linear(path):
    a = np.asarray(Image.open(path).convert('RGB'), np.float32) / 255.0
    return np.where(a <= 0.04045, a / 12.92, ((a + 0.055) / 1.055) ** 2.4)


def photo_sampler(img, metres):
    """The photo (one tile of `metres`) laid over the strip at its real size: repeats a whole number of times along it."""
    h, w = img.shape[:2]
    repeats = max(1, int(round(4.0 / metres)))
    tile_x = W / repeats                       # pixels along a tile
    tile_y = tile_x * PY / PX                  # the same metres up
    ys, xs = np.mgrid[0:H, 0:W].astype(np.float32)

    def sample(ox=0.0, oy=0.0):
        u = ((xs + ox) / tile_x) % 1.0 * w
        v = ((ys + oy) / tile_y) % 1.0 * h
        x0 = np.floor(u).astype(int); y0 = np.floor(v).astype(int)
        fx = (u - x0)[..., None]; fy = (v - y0)[..., None]
        x0 %= w; y0 %= h; x1 = (x0 + 1) % w; y1 = (y0 + 1) % h
        return (img[y0, x0] * (1 - fx) * (1 - fy) + img[y0, x1] * fx * (1 - fy) + img[y1, x0] * (1 - fx) * fy + img[y1, x1] * fx * fy)
    return sample


def luminance(c):
    return c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722


def pattern(c):
    """A photo's pattern: its brightness against its own mean (1 on average)."""
    l = luminance(c)
    return l / max(1e-4, float(l.mean()))


def blur(a, sigma_x, sigma_y):
    """Gaussian blur, wrapping along x (the strip tiles), mirrored up and down."""
    pad = int(3 * sigma_y) + 1
    b = np.concatenate([a[pad:0:-1], a, a[-2:-pad - 2:-1]], axis=0)
    hh, ww = b.shape
    fy = np.fft.fftfreq(hh)[:, None]; fx = np.fft.fftfreq(ww)[None, :]
    g = np.exp(-2 * (np.pi ** 2) * ((fx * sigma_x) ** 2 + (fy * sigma_y) ** 2))
    out = np.real(np.fft.ifft2(np.fft.fft2(b) * g))
    return out[pad:pad + a.shape[0]]


def noise(rng, cells_x, cells_y, octaves=4):
    """Fractal value noise in [-1, 1] over the strip, periodic along x."""
    out = np.zeros((H, W), np.float32); amp = 1.0; total = 0.0
    ys, xs = np.mgrid[0:H, 0:W].astype(np.float32)
    for o in range(octaves):
        cx, cy = cells_x * 2 ** o, cells_y * 2 ** o
        grid = rng.uniform(-1, 1, (cy + 2, cx)).astype(np.float32)
        u = xs / W * cx; v = ys / H * cy
        x0 = np.floor(u).astype(int); y0 = np.floor(v).astype(int)
        fx = u - x0; fy = v - y0
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy)
        x1 = (x0 + 1) % cx; x0 %= cx
        n = (grid[y0, x0] * (1 - fx) * (1 - fy) + grid[y0, x1] * fx * (1 - fy) + grid[y0 + 1, x0] * (1 - fx) * fy + grid[y0 + 1, x1] * fx * fy)
        out += n * amp; total += amp; amp *= 0.5
    return out / total


def line_noise(rng, cells, octaves=3):
    """Periodic 1D noise along x in [-1, 1]."""
    out = np.zeros(W, np.float32); amp = 1.0; total = 0.0
    xs = np.arange(W, dtype=np.float32)
    for o in range(octaves):
        c = cells * 2 ** o
        grid = rng.uniform(-1, 1, c).astype(np.float32)
        u = xs / W * c; x0 = np.floor(u).astype(int); f = u - x0; f = f * f * (3 - 2 * f)
        out += (grid[x0 % c] * (1 - f) + grid[(x0 + 1) % c] * f) * amp; total += amp; amp *= 0.5
    return out / total


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


class Canvas:
    def __init__(self):
        self.albedo = np.zeros((H, W, 3), np.float32)
        self.height = np.zeros((H, W), np.float32)
        self.smooth = np.zeros((H, W), np.float32)
        self.cavity = np.zeros((H, W), np.float32)   # extra darkness for the occlusion (gaps behind boards)


def draw_strokes(canvas, rng, count, y_from, y_to, length, colour, width, raised, droop=True, layer_mask=None):
    """Roots, grass strands: random walks down the wall (x wraps), drawn with a soft disc."""
    v_up = np.linspace(1, 0, H)[:, None]                # v of each row (1 at the top)
    for _ in range(count):
        x = rng.uniform(0, W); y = rng.uniform((1 - y_to) * H, (1 - y_from) * H)
        steps = int(rng.uniform(*length) * PY)
        angle = np.pi / 2 + rng.normal(0, 0.5)          # mostly down
        w = rng.uniform(*width)
        tone = lin(colour) * rng.uniform(0.85, 1.15)
        for s in range(steps):
            angle += rng.normal(0, 0.18)
            if droop: angle = angle * 0.92 + (np.pi / 2) * 0.08
            x = (x + np.cos(angle) * 1.2) % W; y += abs(np.sin(angle)) * 1.0 + 0.2
            if y >= H - 1: break
            r = max(0.6, w * (1 - s / max(1, steps)) ** 0.6)
            xi, yi = int(x), int(y); rr = int(np.ceil(r)) + 1
            ys = np.arange(max(0, yi - rr), min(H, yi + rr + 1)); xs = np.arange(xi - rr, xi + rr + 1) % W
            dy = ys[:, None] - y; dx = ((xs[None, :] - x + W / 2) % W) - W / 2
            d = np.sqrt(dx * dx + dy * dy)
            a = np.clip(r + 0.5 - d, 0, 1)
            if layer_mask is not None: a = a * layer_mask[ys][:, xs]
            sub = canvas.albedo[ys[:, None], xs[None, :]]
            canvas.albedo[ys[:, None], xs[None, :]] = sub * (1 - a[..., None]) + tone * a[..., None]
            hs = canvas.height[ys[:, None], xs[None, :]]
            canvas.height[ys[:, None], xs[None, :]] = np.maximum(hs, hs * (1 - a) + (hs + raised * (1 - d / (r + 1))) * a)


def soil_strip(rng, sources, wet=0.0, slump=0.0, stones=1.0):
    c = Canvas()
    mud, field, grass = sources
    v = np.linspace(1, 0, H)[:, None] * np.ones((1, W))   # 1 at the top edge, 0 at the foot
    # horizons, their boundaries wavy along the wall
    foot_top = 0.11 + 0.06 * slump + 0.06 * line_noise(rng, 9)[None, :] + 0.025 * noise(rng, 40, 6, 2)
    loam_top = 0.64 + 0.05 * line_noise(rng, 5)[None, :]
    turf_bottom = 0.935 + 0.02 * line_noise(rng, 11)[None, :]
    foot = 1 - smooth(foot_top - 0.025, foot_top + 0.025, v)
    humus = smooth(loam_top - 0.06, loam_top + 0.05, v)
    turf = smooth(turf_bottom - 0.01, turf_bottom + 0.012, v)
    # the photos' pattern: fine crumbs of wet earth everywhere, clods of dry field soil at the foot and in the humus
    mud_s = mud(rng.uniform(0, W), rng.uniform(0, H)); field_s = field(rng.uniform(0, W), rng.uniform(0, H))
    crumbs = pattern(mud_s[..., :3]); clods = pattern(field_s[..., :3])
    relief = (mud_s[..., 3] - 0.5) * (1 - foot) + (field_s[..., 3] - 0.5) * foot
    big = noise(rng, 6, 2, 3)
    loam_tone = lin([150, 130, 101]) * (1 + 0.07 * big[..., None]) * np.array([1.0 + 0.03 * wet, 1.0, 1.0 - 0.04 * wet])
    humus_tone = lin([62, 52, 43]) * (1 + 0.1 * noise(rng, 12, 3, 3)[..., None])
    turf_tone = lin([46, 40, 31])
    foot_tone = lin([112, 96, 76]) * (1 - 0.25 * wet)
    # the loam darkens toward the humus in streaks (humus washed down cracks and old roots)
    streaks = np.clip(noise(rng, 90, 3, 2) * 1.6, 0, 1) * smooth(loam_top - 0.25, loam_top, v)
    tone = loam_tone * (1 - 0.35 * streaks[..., None])
    tone = tone * (1 - humus[..., None]) + humus_tone * humus[..., None]
    tone = tone * (1 - turf[..., None]) + turf_tone * turf[..., None]
    tone = tone * (1 - foot[..., None]) + foot_tone * foot[..., None]
    detail = crumbs * (1 - foot) + (0.55 * crumbs + 0.45 * clods) * foot
    detail = detail * (1 - humus) + (0.6 * crumbs + 0.4 * clods) * humus
    c.albedo = tone * np.clip(0.55 + 0.45 * detail, 0.3, 1.8)[..., None]
    # krotovinas: old burrows filled with humus, dark round patches just under the humus
    wobble = noise(rng, 160, 40, 2)
    ys, xs = np.mgrid[0:H, 0:W]
    for _ in range(int(5 + 5 * rng.uniform())):
        x = rng.uniform(0, W); y = (1 - rng.uniform(0.4, 0.64)) * H; r = rng.uniform(0.015, 0.04) * PY
        dx = ((xs - x + W / 2) % W) - W / 2; d = np.sqrt((dx * rng.uniform(0.7, 1.2)) ** 2 + ((ys - y) * 1.3) ** 2) / r + wobble * 0.5
        a = np.clip((1 - d) * 2.5, 0, 1) * 0.55
        c.albedo = c.albedo * (1 - a[..., None]) + humus_tone * a[..., None]
    # relief: the photo's crumbs, the tool marks (scallops of the spade or bucket, 12-30 cm, with fine grooves), clods at the foot
    height = relief * 0.35 + (crumbs - 1) * 0.03
    x = 0.0; marks = np.zeros(W, np.float32)
    while x < W:
        w = rng.uniform(0.12, 0.3) * PX
        xs = np.arange(int(x), int(x + w)) % W
        t = (np.arange(len(xs)) + 0.5) / len(xs)
        marks[xs] = -0.12 * np.sin(np.pi * t) * rng.uniform(0.5, 1.2)
        x += w
    grooves = blur(noise(rng, 220, 2, 2), 0.6, 18) * 0.06
    height = height + marks[None, :] * (1 - foot) * (1 - turf) + grooves * (1 - foot)
    # the humus a little proud of the loam (the loam was cut back more), the turf overhanging
    height = height + 0.06 * humus + 0.1 * turf + 0.25 * foot * (1 + 0.6 * big)
    c.height = height
    c.smooth = 0.07 + 0.03 * humus + (0.16 + 0.12 * wet) * foot
    # pebbles and bits of chalk in the loam
    for _ in range(int(60 * stones)):
        x = rng.uniform(0, W); y = (1 - rng.uniform(0.05, 0.6)) * H
        rx = rng.uniform(2, 7); ry = rx * rng.uniform(0.6, 1.0)
        ys, xs = np.mgrid[max(0, int(y - ry - 2)):min(H, int(y + ry + 3)), int(x - rx - 2):int(x + rx + 3)]
        xs_w = xs % W
        d = np.sqrt(((xs - x) / rx) ** 2 + ((ys - y) / ry) ** 2)
        a = np.clip((1 - d) * 3, 0, 1)
        grey = lin([168, 160, 142]) * rng.uniform(0.8, 1.1)
        c.albedo[ys, xs_w] = c.albedo[ys, xs_w] * (1 - a[..., None]) + grey * a[..., None]
        c.height[ys, xs_w] += a * 0.25
    # roots: many fine ones in the humus and turf, a few thicker down into the loam
    humus_mask = humus
    draw_strokes(c, rng, 110, 0.7, 0.97, (0.02, 0.12), [104, 90, 72], (0.35, 0.7), 0.08, layer_mask=None)
    draw_strokes(c, rng, 9, 0.6, 0.92, (0.1, 0.32), [118, 100, 78], (0.9, 1.7), 0.2)
    # dry grass hanging over the top edge
    draw_strokes(c, rng, 300, 0.965, 1.0, (0.015, 0.07), [118, 106, 72], (0.4, 0.9), 0.15)
    return c


def boards_strip(rng, sources, base):
    """Boards laid flat behind round posts, weathered; soil above and in the gaps; some broken or gone."""
    c = Canvas(); c.albedo = base.albedo.copy(); c.height = base.height.copy(); c.smooth = base.smooth.copy()
    mud = sources[0]
    top = rng.uniform(0.72, 0.8)
    y = 0.04
    grain_src = noise(rng, 36, 260, 3)
    dirt_src = noise(rng, 24, 10, 3)
    while y < top:
        h = rng.uniform(0.14, 0.2) / 1.3
        gap = rng.uniform(0.004, 0.015) / 1.3
        y0, y1 = y + gap, min(top, y + gap + h)
        # a board's run along the wall: pieces of 1.5-4 m with joints, a few broken out
        x = rng.uniform(0, W); end = x + W
        while x < end:
            length = min(rng.uniform(1.5, 4.0) * PX, end - x + 4)
            broken = rng.uniform() < 0.1
            sag = rng.uniform(-0.01, 0.01)
            r0 = int((1 - y1 - sag) * H); r1 = int((1 - y0 - sag) * H)
            xs = (np.arange(int(x), int(x + length - rng.uniform(2, 6))) % W)
            if not broken and r1 > r0:
                rows = np.arange(max(0, r0), min(H, r1))
                tone = lin([112, 102, 90]) * rng.uniform(0.8, 1.1) * np.array([1.0, rng.uniform(0.97, 1.02), rng.uniform(0.92, 1.0)])
                grain = grain_src[rows][:, xs]
                t = (rows - r0) / max(1, r1 - r0)
                bevel = np.minimum(t, 1 - t)[:, None]
                wet = np.clip(1.0 - (1 - rows[:, None] / H) * 2.2, 0, 1)   # wetter and dirtier low on the wall
                col = tone * (1 + 0.3 * grain[..., None]) * (1 - 0.4 * wet[..., None])
                # mud thrown and washed onto the boards: more near the floor and the top edge
                low = (rows[:, None] / H) ** 3
                dirt = np.clip(dirt_src[rows][:, xs] * 1.4 + low * 1.2 - 0.15, 0, 0.85)
                col = col * (1 - dirt[..., None]) + base.albedo[rows[:, None], xs[None, :]] * 0.85 * dirt[..., None]
                c.albedo[rows[:, None], xs[None, :]] = col
                c.height[rows[:, None], xs[None, :]] = 0.55 + 0.25 * np.clip(bevel * 12, 0, 1) + 0.03 * grain
                c.smooth[rows[:, None], xs[None, :]] = 0.12
                # knots
                for _ in range(int(length / PX * rng.uniform(0, 1.2))):
                    kx = int(rng.uniform(0, len(xs))); ky = rng.uniform(0.3, 0.7) * (r1 - r0) + r0
                    kr = rng.uniform(2, 5)
                    yy, xx = np.mgrid[max(0, int(ky - kr - 1)):min(H, int(ky + kr + 2)), kx - int(kr) - 1:kx + int(kr) + 2]
                    d = np.sqrt((xx - kx) ** 2 / 1.6 + (yy - ky) ** 2) / kr
                    a = np.clip(1 - d, 0, 1)
                    xw = xs[np.clip(xx, 0, len(xs) - 1)]
                    c.albedo[yy, xw] *= (1 - 0.5 * a)[..., None]
            x += length
        # the gap under the board: soil behind, deep
        rg0 = int((1 - y0) * H); rg1 = int((1 - y + 1e-4) * H)
        if rg1 > rg0:
            rows = np.arange(max(0, rg0), min(H, rg1 + 1))
            c.albedo[rows] *= 0.45; c.height[rows] = 0.0; c.cavity[rows] = 0.5
        y = y1
    # posts in front, every 1.4-2 m, round, weathered grey, their tops ragged
    x = rng.uniform(0, PX)
    while x < W:
        w = rng.uniform(0.1, 0.14) * PX
        post_top = rng.uniform(top + 0.02, 0.95)
        rows = np.arange(int((1 - post_top) * H), H)
        xs = np.arange(int(x - w / 2), int(x + w / 2)) % W
        t = (np.arange(len(xs)) + 0.5) / len(xs) * 2 - 1
        profile = np.sqrt(np.clip(1 - t * t, 0, 1))
        tone = lin([104, 96, 86]) * rng.uniform(0.85, 1.1)
        streak = noise(rng, 4, 30, 2)[rows][:, xs]
        c.albedo[rows[:, None], xs[None, :]] = tone * (0.75 + 0.25 * profile[None, :, None]) * (1 + 0.12 * streak[..., None])
        c.height[rows[:, None], xs[None, :]] = 0.85 + 0.15 * profile[None, :]
        c.smooth[rows[:, None], xs[None, :]] = 0.1
        x += rng.uniform(1.4, 2.0) * PX
    return c


def wattle_strip(rng, sources, base):
    """Rods woven round stakes, grey bark, the soil above them and in the gaps."""
    c = Canvas(); c.albedo = base.albedo.copy(); c.height = base.height.copy(); c.smooth = base.smooth.copy()
    top = rng.uniform(0.72, 0.82)
    spacing = rng.uniform(0.32, 0.4) * PX
    stakes = np.arange(0, W, spacing)
    # shift so the last stake wraps onto the first
    spacing = W / len(stakes); stakes = np.arange(len(stakes)) * spacing + rng.uniform(0, spacing)
    xs = np.arange(W, dtype=np.float32)
    phase_x = ((xs - stakes[0]) % (2 * spacing)) / (2 * spacing)         # 0-1 over two stake intervals
    bark = noise(rng, 50, 30, 3)
    y = 0.03; k = 0
    while y < top:
        d = rng.uniform(0.02, 0.042) / 1.3
        yc = y + d / 2
        # over and under: the rod comes forward between two stakes, goes behind at every other one
        weave = np.cos(2 * np.pi * phase_x + np.pi * (k % 2))
        offset = weave * 0.004 / 1.3 + line_noise(rng, 6) * 0.004 / 1.3
        rows_c = (1 - (yc + offset)) * H
        r = d / 2 * H
        rows = np.arange(max(0, int(rows_c.min() - r - 1)), min(H, int(rows_c.max() + r + 2)))
        dy = (rows[:, None] - rows_c[None, :]) / r
        inside = np.clip(1 - dy * dy, 0, 1)
        a = np.clip((1 - np.abs(dy)) * 4, 0, 1)
        front = 0.5 + 0.5 * weave[None, :]
        tone = lin([96, 86, 72]) * rng.uniform(0.75, 1.2) * np.array([1.0, rng.uniform(0.97, 1.02), rng.uniform(0.9, 1.0)])
        col = tone * (0.7 + 0.3 * np.sqrt(inside)[..., None]) * (1 + 0.2 * bark[rows][..., None]) * (0.75 + 0.25 * front[..., None])
        c.albedo[rows] = c.albedo[rows] * (1 - a[..., None]) + col * a[..., None]
        c.height[rows] = c.height[rows] * (1 - a) + (0.45 + 0.3 * front + 0.2 * np.sqrt(inside)) * a
        c.smooth[rows] = c.smooth[rows] * (1 - a) + 0.1 * a
        y += d + rng.uniform(0.0, 0.006) / 1.3
        k += 1
    # stakes, thin, in front where the rods go behind
    for sx in stakes:
        w = rng.uniform(0.03, 0.05) * PX
        stake_top = rng.uniform(top, top + 0.1)
        rows = np.arange(int((1 - stake_top) * H), H)
        cols = np.arange(int(sx - w / 2), int(sx + w / 2)) % W
        t = (np.arange(len(cols)) + 0.5) / len(cols) * 2 - 1
        profile = np.sqrt(np.clip(1 - t * t, 0, 1))
        tone = lin([88, 78, 64]) * rng.uniform(0.85, 1.1)
        c.albedo[rows[:, None], cols[None, :]] = tone * (0.7 + 0.3 * profile[None, :, None])
        c.height[rows[:, None], cols[None, :]] = 0.9 + 0.1 * profile[None, :]
    return c


def logs_strip(rng, sources, bark):
    """Round timber: the bark scan once round the log (y: 0 the underside, 0.5 the top) and three times along its 4 m (x);
    greyed and weathered, mud on the underside, soil of the cover on the top, a few cracks along the grain."""
    colour, height, gloss = bark
    bh, bw = colour.shape[:2]                  # the scan: x round the trunk (0.6 m), y along it (1.2 m)
    ys, xs = np.mgrid[0:H, 0:W].astype(np.float32)
    along = (xs / (W / 3.0)) % 1.0 * bh        # the scan's y along the log
    round_ = (ys / H) % 1.0 * bw               # the scan's x round it
    y0 = np.floor(along).astype(int) % bh; x0 = np.floor(round_).astype(int) % bw
    c = Canvas()
    c.albedo = colour[y0, x0].copy()
    c.height = height[y0, x0].copy()
    c.smooth = gloss[y0, x0].copy() * 0.6
    # weathered: greyer and a little lighter, as timber left out for a season or two
    lum = luminance(c.albedo)[..., None]
    c.albedo = (c.albedo * 0.55 + lum * 0.45) * np.array([1.0, 0.98, 0.95]) * 1.1
    v = ys / H
    mud, field, _ = sources
    soil = field(0, 37)[..., :3]
    # the underside lay in the mud (v about 0), the top (v about 0.5) under the soil of the cover: patches of it left on
    cover = np.clip(np.cos((v - 0.5) * 2 * np.pi), 0, 1) ** 2 * np.clip(noise(rng, 18, 6, 3) * 1.6 - 0.35, 0, 1)
    under = np.clip(np.cos(v * 2 * np.pi), 0, 1) ** 3 * 0.55
    dirt = np.clip(cover + under, 0, 0.9)[..., None]
    c.albedo = c.albedo * (1 - dirt) + soil * 0.8 * dirt
    c.height = c.height * (1 - dirt[..., 0] * 0.5) + 0.35 * dirt[..., 0]
    # a few dry cracks along the grain
    for _ in range(14):
        row = rng.uniform(0, H); length = rng.uniform(0.3, 1.4) * PX; x = rng.uniform(0, W)
        cols = (np.arange(int(x), int(x + length)) % W)
        wob = (np.sin(np.arange(len(cols)) / rng.uniform(40, 90)) * rng.uniform(1, 3) + row).astype(int) % H
        c.albedo[wob, cols] *= 0.55; c.height[wob, cols] = 0.0
    return c


def normals(height, strength):
    """OpenGL normal map from a height field (x wraps; v up = image rows going up)."""
    hx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5
    hy = np.zeros_like(height)
    hy[1:-1] = (height[:-2] - height[2:]) * 0.5                       # d/dv: the row above minus the row below
    n = np.stack([-hx * strength, -hy * strength, np.ones_like(height)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def occlusion(height, cavity):
    near = blur(height, 6, 6)
    far = blur(height, 24, 24)
    ao = 1 - np.clip((near - height) * 1.6, 0, 0.45) - np.clip((far - height) * 0.8, 0, 0.25) - cavity
    return np.clip(ao, 0.25, 1)


def main():
    package = sys.argv[1]
    ground = os.path.join(package, 'Environment', 'Textures', 'Ground', 'PolyHaven')
    out = os.path.join(package, 'Textures', 'Trenches')
    os.makedirs(out, exist_ok=True)
    def photo(name, metres):
        # the set's colour, with its height (R of its mask) in a fourth channel
        colour = load_linear(os.path.join(ground, name, name + '_diff.jpg'))
        height = np.asarray(Image.open(os.path.join(ground, name, name + '_mask.jpg')).convert('RGB'), np.float32)[..., :1] / 255.0
        return photo_sampler(np.concatenate([colour, height], axis=-1), metres)
    mud = photo('brown_mud_03', 1.3)
    field = photo('dry_mud_field_001', 3.0)
    grass = photo('grass_ground', 2.5)
    sources = (mud, field, grass)
    rng = np.random.default_rng(1945)
    strips = []
    a = soil_strip(rng, sources); strips.append(a)
    b = soil_strip(rng, sources, wet=0.6, slump=0.8, stones=1.6); strips.append(b)
    strips.append(boards_strip(rng, sources, soil_strip(rng, sources, wet=0.3)))
    strips.append(wattle_strip(rng, sources, soil_strip(rng, sources, wet=0.2)))
    oak = os.path.join(package, 'Environment', 'Vegetation', 'Generated', 'Oak')
    bark_mask = np.asarray(Image.open(os.path.join(oak, 'oak_dead_bark_mask.png')).convert('RGBA'), np.float32) / 255.0
    strips.append(logs_strip(rng, sources, (load_linear(os.path.join(oak, 'oak_dead_bark_albedo.png')), bark_mask[..., 2], bark_mask[..., 3])))
    albedo = np.zeros((H * STRIPS, W, 3), np.float32)
    normal = np.zeros((H * STRIPS, W, 3), np.float32)
    mask = np.zeros((H * STRIPS, W, 4), np.float32)
    for k, s in enumerate(strips):
        rows = slice(H * (STRIPS - 1 - k), H * (STRIPS - k))     # strip 0 at the bottom of the image (v 0-0.25)
        albedo[rows] = s.albedo
        normal[rows] = normals(s.height, 5.0)
        # and the trench's own: little sky reaches the foot of a wall deeper than the trench is wide
        v = np.linspace(1, 0, H)[:, None]
        mask[rows, :, 1] = occlusion(s.height, s.cavity) * (0.5 + 0.5 * v ** 0.7)
        mask[rows, :, 3] = np.clip(s.smooth, 0, 1)
    Image.fromarray((srgb(albedo) * 255 + 0.5).astype(np.uint8), 'RGB').save(os.path.join(out, 'trench_walls_albedo.png'))
    Image.fromarray((normal * 255 + 0.5).astype(np.uint8), 'RGB').save(os.path.join(out, 'trench_walls_normal.png'))
    Image.fromarray((np.clip(mask, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(out, 'trench_walls_mask.png'))
    print('wrote', out)


if __name__ == '__main__':
    main()
