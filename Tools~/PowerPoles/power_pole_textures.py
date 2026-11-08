"""Power pole atlas for GeoWorld (GeoPowerPole): one 2048 x 2048 atlas, everything drawn here (no photo sources), so it
carries no licence of its own. Its layout is GeoPowerPole's (UV origin at the bottom left, as Unity's):

  x    0- 255  the shaft of an СВ-95 concrete pole unrolled, weathered: u round its outline (the +z face, a bevel, the +x face,
               a bevel, the -z face with the crossarm, a bevel, the -x face, a bevel; GeoPowerPole.Around), v up it from
               0.35 m under the ground to its top at 7.6 m. Vibrated concrete with its bug holes, form marks and the rough trowelled
               open face (-x), chipped edges, rain streaks from the top, rust run down from the strap and the bolt holes, soil
               splashed up from the ground, lichen, the earthing wire down the -x face held by clamps
  x  256- 511  the same pole older: darker, more lichen and rust, its foot whitewashed with lime (as village poles and trees
               are), peeling
  x  512- 767  a pole hit by shell fragments: pits and chips with the aggregate showing, a few down to the rebar, dust
  x  768-1023  spare (plain concrete)
  x 1024-2047, y 1792-2047  the crossarm, 63 x 5 steel angle: u along its 1.5 m, v round its section (the top of the
               horizontal leg, its edge, its underside, the outer face of the vertical leg, its edge); weathered galvanising
               with rust, worst at the ends and round the pins, bird droppings on top
  x 1024-2047, y 1536-1791  rusty steel (pins, nuts, the strap, rebar)
  x 1024-1535, y 1280-1535  the ТФ-20 porcelain insulator: u round it, v along its profile from the foot; glazed white, dirty
               on what faces up, the aluminium tie wire in the neck
  x 1536-2047, y 1280-1535  the pole's top: rough concrete, moss, droppings
  x 1024-1535, y  768-1279  a fracture of the concrete: cement and gravel (granite, limestone, basalt)
  the rest     spare (plain concrete)

Writes Textures/PowerPoles/power_pole_albedo.png (sRGB), _normal.png (OpenGL, as Unity), _mask.png (R metallic, G occlusion,
A smoothness: URP Lit's metallic-gloss and occlusion maps both).

    py -I Tools~/PowerPoles/power_pole_textures.py <package folder>
"""
import math
import os
import sys

import numpy as np
from PIL import Image

N = 2048
RNG = np.random.default_rng(20261108)

# GeoPowerPole's dimensions (keep in step)
HEIGHT, BURIED, DEPTH, FOOT, TOP, CHAMFER = 7.6, 0.35, 0.165, 0.224, 0.165, 0.015
ARM_TOP, ARM_HALF, ARM_LEG, ARM_T = 7.32, 0.75, 0.063, 0.005
PIN_X = (-0.62, -0.21, 0.21, 0.62)
INSULATOR = [(0.012, 0.0), (0.034, 0.010), (0.028, 0.021), (0.020, 0.033), (0.020, 0.050), (0.025, 0.056), (0.019, 0.063),
             (0.024, 0.071), (0.017, 0.081), (0.0, 0.085)]
NECK_RING = 6


def half_width(y):
    return 0.5 * (FOOT + (TOP - FOOT) * y / HEIGHT)


def around():
    """The share of the outline up to each corner at mid-height (GeoPowerPole.Outline)."""
    hx, hz, c = half_width(HEIGHT / 2), DEPTH / 2, CHAMFER
    o = [(-(hx - c), hz), (hx - c, hz), (hx, hz - c), (hx, -(hz - c)), (hx - c, -hz), (-(hx - c), -hz), (-hx, -(hz - c)), (-hx, hz - c)]
    d = [math.dist(o[i], o[(i + 1) % 8]) for i in range(8)]
    total = sum(d)
    out, run = [], 0.0
    for i in range(8):
        out.append(run / total)
        run += d[i]
    return out + [1.0]


AROUND = around()


# ---------------------------------------------------------------------------------------------------------- helpers

def lin(c):
    c = np.asarray(c, np.float32) / 255.0
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def srgb(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def fnoise(h, w, sx, sy, beta=0.0, rng=RNG):
    """Periodic noise (FFT-filtered white noise) with features about sx by sy pixels: zero mean, unit deviation. beta > 0
    adds finer octaves (1/f^beta below the cut-off)."""
    white = rng.standard_normal((h, w)).astype(np.float32)
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.rfftfreq(w)[None, :]
    r = np.sqrt((fx * sx) ** 2 + (fy * sy) ** 2)
    filt = np.exp(-(r ** 2) * 4.0)
    if beta > 0:
        filt = filt + 0.35 * np.where(r > 0, np.minimum(1.0, (1.0 / np.maximum(r, 1e-6)) ** beta) * np.exp(-r * 0.15), 0)
    out = np.fft.irfft2(np.fft.rfft2(white) * filt, s=(h, w)).astype(np.float32)
    out -= out.mean()
    return out / (out.std() + 1e-8)


def smooth(a, k):
    """A periodic box blur of k pixels (twice: about Gaussian)."""
    if k < 2:
        return a
    out = a
    for _ in range(2):
        c = np.cumsum(np.concatenate([out[:, -k:], out, out[:, :k]], 1), 1)
        out = (c[:, 2 * k:] - c[:, :-2 * k])[:, :a.shape[1]] / (2 * k)
        c = np.cumsum(np.concatenate([out[-k:], out, out[:k]], 0), 0)
        out = (c[2 * k:] - c[:-2 * k])[:a.shape[0]] / (2 * k)
    return out.astype(np.float32)


def splat(target, xs, ys, radii, depth, wrap=True, shape='dome', op='add'):
    """Stamps round dips or bumps (depth < 0: a dip) at the points into target (rows: y up)."""
    h, w = target.shape
    rmax = int(math.ceil(max(radii))) + 1
    for dy in range(-rmax, rmax + 1):
        for dx in range(-rmax, rmax + 1):
            d = math.hypot(dx, dy)
            t = d / np.maximum(radii, 1e-3)
            inside = t < 1
            if not inside.any():
                continue
            prof = (1 - t ** 2) if shape == 'dome' else np.where(t < 0.7, 1.0, (1 - t) / 0.3)
            prof = np.where(inside, prof, 0).astype(np.float32)
            x = (xs + dx) % w if wrap else np.clip(xs + dx, 0, w - 1)
            y = np.clip(ys + dy, 0, h - 1)
            val = prof * depth
            if op == 'add':
                np.add.at(target, (y, x), val)
            elif op == 'min':
                np.minimum.at(target, (y, x), np.where(inside, val, 0))
            else:
                np.maximum.at(target, (y, x), val)


def normal_from_height(hmm, px_mm_x, px_mm_y, strength=1.0):
    """Tangent-space normals (OpenGL: green up the texture) from a height in millimetres (rows: y up)."""
    gx = (np.roll(hmm, -1, 1) - np.roll(hmm, 1, 1)) / (2 * px_mm_x)
    gy = (np.roll(hmm, -1, 0) - np.roll(hmm, 1, 0)) / (2 * px_mm_y)
    n = np.stack([-gx * strength, -gy * strength, np.ones_like(hmm)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n


def cavity(hmm, k, amount):
    """Occlusion from how far a texel lies under its surroundings."""
    return np.clip(1 - np.maximum(0, smooth(hmm, k) - hmm) * amount, 0.25, 1).astype(np.float32)


# ---------------------------------------------------------------------------------------------------------- the shaft

SW, SH = 256, 2048
PX_V = (HEIGHT + BURIED) / SH * 1000            # mm a pixel up
PERIM = 2 * (2 * (half_width(HEIGHT / 2) - CHAMFER)) + 2 * (DEPTH - 2 * CHAMFER) + 4 * CHAMFER * math.sqrt(2)
PX_U = PERIM / SW * 1000                        # mm a pixel round


def row_of(y):
    return int(round((y + BURIED) / (HEIGHT + BURIED) * SH))


def face_span(face):
    """Pixel columns of a flat face (0 +z, 1 +x, 2 -z, 3 -x) and of the whole outline position of each corner."""
    a, b = AROUND[face * 2], AROUND[face * 2 + 1]
    return int(round(a * SW)), int(round(b * SW))


def edges_px():
    return [int(round(a * SW)) % SW for a in AROUND[:8]]


def shaft(kind, rng):
    """One strip: albedo (linear RGB), height (mm), metallic, occlusion, smoothness. kind: weathered, aged, scarred."""
    h, w = SH, SW
    ys = (np.arange(h, dtype=np.float32)[:, None] + 0.5) / h * (HEIGHT + BURIED) - BURIED   # metres, per row
    ys = np.broadcast_to(ys, (h, w))
    us = np.broadcast_to((np.arange(w, dtype=np.float32)[None, :] + 0.5) / w, (h, w))

    # concrete: a grey with a little warmth; older is darker
    base = {'weathered': (148, 147, 142), 'aged': (127, 126, 121), 'scarred': (145, 143, 137)}[kind]
    col = np.broadcast_to(lin(base), (h, w, 3)).copy()
    height = np.zeros((h, w), np.float32)

    # cloudy tone of the cast (large) and the cement's mottle (small)
    big = fnoise(h, w, 60, 220, rng=rng)
    mid = fnoise(h, w, 10, 14, rng=rng)
    fine = fnoise(h, w, 1.6, 1.6, rng=rng)
    tone = 1 + 0.07 * big + 0.045 * mid + 0.05 * fine
    col *= tone[..., None]
    height += 0.06 * mid + 0.05 * fine

    # sand grains: light and dark specks
    specks = rng.random((h, w))
    col *= np.where(specks > 0.985, 1.25, np.where(specks < 0.012, 0.7, 1.0))[..., None]

    # bug holes: the air voids of vibrated concrete (more on the faces that lay in the form's sides)
    n = 2600
    bx = rng.integers(0, w, n); by = rng.integers(0, h, n)
    br = rng.uniform(0.6, 2.4, n) * (1 + (rng.random(n) < 0.08) * 1.3)
    holes = np.zeros((h, w), np.float32)
    splat(holes, bx, by, br, -1.0)
    height += holes * 1.2
    col *= (1 + 0.35 * np.clip(holes, -1, 0))[..., None]

    # the open face of the mould (-x): rough, trowelled lengthwise
    a3, b3 = face_span(3)
    open_face = np.zeros((h, w), np.float32)
    open_face[:, a3:b3] = 1
    open_face = smooth(open_face, 2)
    trowel = fnoise(h, w, 2.5, 90, rng=rng)
    height += open_face * (0.25 * trowel + 0.12 * fine)
    col *= (1 + open_face * (0.04 * trowel - 0.03))[..., None]

    # form marks: faint lines across the formed faces every so often
    for yy in rng.uniform(0, HEIGHT, 6):
        r0 = row_of(yy)
        band = np.exp(-((np.arange(h) - r0) / 1.2) ** 2)[:, None] * (1 - open_face)
        height -= 0.15 * band
        col *= (1 - 0.03 * band)[..., None]

    # chipped edges: where the bevels meet the faces
    ex = edges_px()
    chips = np.zeros((h, w), np.float32)
    for e in ex:
        k = 70 if kind != 'scarred' else 160
        cy = rng.integers(0, h, k); cx = (e + rng.integers(-2, 3, k)) % w
        cr = rng.uniform(1.0, 5.0, k) * np.where(rng.random(k) < 0.15, 2.2, 1)
        splat(chips, cx, cy, cr, -1.0, shape='flat')
    chips = np.clip(chips, -1, 0)
    height += chips * 2.5
    aggregate = fnoise(h, w, 2.2, 2.2, rng=rng)
    col = col * (1 + chips[..., None] * 0.25) + (-chips)[..., None] * (lin((158, 150, 138)) * (0.15 + 0.1 * aggregate[..., None]))

    # the bolt holes near the top, through the z faces (one above the crossarm, one lower)
    for face in (0, 2):
        a, b = face_span(face)
        cxp = (a + b) // 2
        for yy in (7.48, 7.05):
            r0 = row_of(yy)
            yy_, xx_ = np.ogrid[:h, :w]
            d = np.hypot((xx_ - cxp) * PX_U / PX_V, yy_ - r0) * PX_V / 11
            hole = np.clip(1.2 - d, 0, 1)
            height -= 6 * hole
            col *= (1 - 0.75 * np.clip((1.05 - d) * 4, 0, 1))[..., None]

    # rain: streaks washed lighter and run darker from the top down
    streak = fnoise(h, w, 12, 520, rng=rng)
    thin = fnoise(h, w, 2.2, 160, rng=rng)
    fall = np.clip((ys - 1.0) / (HEIGHT - 1.0), 0, 1)
    dirt_streak = (np.clip(streak, 0, None) * 0.6 + np.clip(thin - 1.3, 0, None)) * (0.3 + 0.7 * fall)
    wash = np.clip(-streak, 0, None) * fall
    col *= (1 - (0.09 if kind != 'aged' else 0.15) * dirt_streak)[..., None]
    col *= (1 + 0.04 * wash)[..., None]
    # the top darker from water standing on it
    col *= (1 - 0.12 * np.clip((ys - (HEIGHT - 0.25)) / 0.25, 0, 1))[..., None]

    # rust run down from the strap (the -z face and its bevels) and from the bolt holes
    rust = np.zeros((h, w), np.float32)
    a2, b2 = face_span(2)
    starts = [((a2 + b2) / 2 + rng.uniform(-0.4, 0.4) * (b2 - a2), ARM_TOP - 0.03, rng.uniform(0.6, 1.8), rng.uniform(1.5, 4)) for _ in range(5)]
    starts += [(ex[3] + rng.uniform(-3, 3), ARM_TOP - 0.03, rng.uniform(0.3, 0.9), 2.5), (ex[6] + rng.uniform(-3, 3), ARM_TOP - 0.03, rng.uniform(0.3, 0.9), 2.5)]
    for face in (0, 2):
        a, b = face_span(face)
        for yy in (7.48, 7.05):
            starts.append(((a + b) / 2, yy - 0.012, rng.uniform(0.25, 0.9), 2.0))
    if kind == 'aged':
        starts = [(x, y, l * 1.6, wdt * 1.3) for x, y, l, wdt in starts]
    meander = fnoise(h, w, 4, 120, rng=rng)
    for (x0, y0, length, width) in starts:
        below = np.clip((y0 - ys) / length, 0, None)
        alpha = np.where(ys <= y0, np.exp(-below * 2.2), 0) * np.clip((y0 - ys) / 0.02 + 0.3, 0, 1)
        xc = x0 + meander * 1.5
        dx = np.abs(((us * w - xc + w / 2) % w) - w / 2)
        rust += alpha * np.exp(-(dx / width) ** 2) * (0.6 + 0.4 * np.clip(fine * 0.5 + 0.5, 0, 1))
    rust = np.clip(rust, 0, 1)
    rust_col = lin((120, 72, 40))
    col = col * (1 - 0.6 * rust[..., None]) + rust_col * 0.6 * rust[..., None]

    # soil splashed up from the ground, and the part in it
    splash = np.clip(1 - ys / (0.35 + 0.12 * big), 0, 1) ** 1.5
    splash *= 0.6 + 0.4 * np.clip(mid * 0.5 + 0.5, 0, 1)
    soil = lin((88, 72, 55))
    col = col * (1 - 0.7 * splash[..., None]) + soil * 0.7 * splash[..., None]
    under = np.clip(-ys / 0.04, 0, 1)
    col = col * (1 - under[..., None]) + lin((62, 52, 40)) * (0.8 + 0.2 * mid[..., None]) * under[..., None]

    # lichen: grey-green and yellow rosettes, more on one face and low down
    lichen = np.zeros((h, w), np.float32)
    nl = {'weathered': 90, 'aged': 260, 'scarred': 60}[kind]
    lx = rng.integers(0, w, nl)
    ly = (np.abs(rng.normal(0, 0.35, nl)) * SH).astype(int).clip(0, h - 1)
    a1, b1 = face_span(1)
    lx = np.where(rng.random(nl) < 0.55, rng.integers(a1, b1, nl), lx)
    splat(lichen, lx, ly, rng.uniform(1.5, 7, nl), 1.0, op='max')
    lichen = np.clip(lichen * (0.7 + 0.6 * fine), 0, 1)
    lichen_col = np.where((fnoise(h, w, 30, 30, rng=rng) > 0.6)[..., None], lin((160, 150, 92)), lin((128, 134, 112)))
    la = 0.75 if kind == 'aged' else 0.6
    col = col * (1 - la * lichen[..., None]) + lichen_col * la * lichen[..., None]
    height += lichen * 0.3

    metallic = np.zeros((h, w), np.float32)
    smooth_ = 0.2 + 0.04 * mid - 0.06 * open_face + 0.12 * splash * 0.5 - 0.08 * lichen

    # the earthing wire down the -x face from the crossarm, in clamps every 1.4 m (every look has it)
    wx = a3 + int((b3 - a3) * 0.62)
    wire_rows = np.arange(h)
    top_row = row_of(ARM_TOP - 0.07)
    dxw = np.abs(np.arange(w) - wx)[None, :] * PX_U
    on = (wire_rows[:, None] < top_row) & (wire_rows[:, None] > row_of(-0.05))
    rw = 4.0  # mm, radius
    prof = np.clip(1 - (dxw / rw) ** 2, 0, 1)
    wire = np.where(on, np.sqrt(prof), 0).astype(np.float32)
    height = np.maximum(height, wire * rw * 1.4)
    wire_col = lin((70, 52, 40)) * (0.8 + 0.4 * np.clip(fine * 0.3 + 0.5, 0, 1))[..., None]
    col = col * (1 - wire[..., None]) + wire_col * wire[..., None]
    metallic = np.maximum(metallic, wire * 0.25)
    for yy in np.arange(0.6, ARM_TOP - 0.2, 1.4):
        r0 = row_of(yy)
        clamp = np.zeros((h, w), np.float32)
        clamp[max(0, r0 - 3):r0 + 3, max(0, wx - 7):wx + 8] = 1
        clamp = smooth(clamp, 1)
        height = np.maximum(height, clamp * 1.2)
        col = col * (1 - clamp[..., None]) + lin((92, 70, 52)) * clamp[..., None]
    # rust from the wire onto the face
    stain = np.exp(-(np.abs(np.arange(w) - wx)[None, :] / 5.0) ** 2) * on * 0.25 * np.clip(streak * 0.5 + 0.6, 0, 1)
    col = col * (1 - stain[..., None] * 0.5) + rust_col * stain[..., None] * 0.5

    if kind == 'aged':
        # lime whitewash on the foot, uneven top edge, peeling and grey with dirt
        edge = 1.1 + 0.05 * fnoise(h, w, 12, 400, rng=rng)[:1, :] + 0.03 * fnoise(h, w, 2, 400, rng=rng)[:1, :]
        drip = np.clip(fnoise(h, w, 1.8, 30, rng=rng), 0, None) * 0.08
        lime = ((ys < edge - drip) & (ys > -0.02)).astype(np.float32)
        peel = (fnoise(h, w, 10, 14, rng=rng) + 0.3 * fine) > 1.25
        lime *= np.where(peel, 0.0, 1.0)
        lime = smooth(lime, 1) * np.clip(1 - (ys - (edge - 0.2)) / 0.35, 0.55, 1) * (0.85 + 0.15 * np.clip(mid * 0.5 + 0.5, 0, 1))
        lime_col = lin((214, 212, 200)) * (0.85 + 0.1 * mid[..., None])
        dirt = np.clip(1 - ys / 0.4, 0, 1)[..., None] * 0.35
        lime_col = lime_col * (1 - dirt) + soil * dirt
        col = col * (1 - lime[..., None] * 0.92) + lime_col * lime[..., None] * 0.92
        height += lime * 0.25
        smooth_ = smooth_ - lime * 0.08

    if kind == 'scarred':
        # shell fragments: pits from millimetres to a few centimetres, densest 0.5-3 m up on one side
        pits = np.zeros((h, w), np.float32)
        np_ = 520
        px_ = np.where(rng.random(np_) < 0.6, rng.normal(w * 0.3, w * 0.18, np_), rng.uniform(0, w, np_)).astype(int) % w
        py_ = np.clip(rng.gamma(2.2, 0.55, np_) / (HEIGHT + BURIED) * SH + row_of(0.2), 0, h - 1).astype(int)
        pr = np.clip(rng.lognormal(1.0, 0.65, np_), 1.0, 20)
        splat(pits, px_, py_, pr, -1.0, shape='flat')
        pits = np.clip(pits, -1, 0)
        rough = 0.6 + 0.4 * np.clip(aggregate * 0.5 + 0.5, 0, 1)
        height += pits * 4 * rough
        fresh = lin((165, 160, 150)) * (0.8 + 0.3 * aggregate[..., None])
        col = col * (1 + pits[..., None]) + fresh * (-pits)[..., None]
        # a few gouges down to the rebar
        for _ in range(3):
            cx = rng.integers(0, w); cy = row_of(rng.uniform(0.6, 3.2))
            yy_, xx_ = np.ogrid[:h, :w]
            dxx = np.abs(((xx_ - cx + w / 2) % w) - w / 2)
            g = np.clip(1 - np.hypot(dxx / 9, (yy_ - cy) / 26), 0, 1)
            height -= 8 * g
            col = col * (1 - 0.5 * g[..., None]) + lin((150, 145, 135)) * 0.5 * g[..., None]
            bar = (g > 0.35) & (np.abs(xx_ - cx) < 2)
            col = np.where(bar[..., None], lin((96, 52, 30)), col)
            height = np.where(bar, height + 3, height)
        # dust of the fighting: a beige film low down
        dust = np.clip(1 - ys / 2.5, 0, 1) * 0.35 * (0.6 + 0.4 * np.clip(big * 0.5 + 0.5, 0, 1))
        col = col * (1 - dust[..., None]) + lin((176, 164, 140)) * dust[..., None]

    occ = cavity(height, 3, 0.35) * cavity(height, 12, 0.08)
    return col, height, metallic, occ, np.clip(smooth_, 0.05, 0.6)


# ---------------------------------------------------------------------------------------------------------- the rest

def crossarm(rng):
    """1024 x 256: u along the angle, v round its section (from the top face's inner edge)."""
    w, h = 1024, 256
    seg = [ARM_LEG, ARM_T, ARM_LEG - ARM_T, ARM_LEG - ARM_T, ARM_T]
    cum = np.cumsum([0] + seg) / sum(seg)
    v = (np.arange(h)[:, None] + 0.5) / h
    u = (np.arange(w)[None, :] + 0.5) / w
    top_face = (v < cum[1]).astype(np.float32)
    under = ((v >= cum[2]) & (v < cum[3])).astype(np.float32)
    outer = ((v >= cum[3]) & (v < cum[4])).astype(np.float32)
    big = fnoise(h, w, 40, 25, rng=rng)
    mid = fnoise(h, w, 6, 4, rng=rng)
    fine = fnoise(h, w, 1.5, 1.5, rng=rng)
    zinc = lin((120, 121, 119)) * (1 + 0.08 * big + 0.06 * mid)[..., None]
    # rust: at the ends, round the pins, along the lower edges, and blooming through the zinc
    pins = np.zeros((1, w), np.float32)
    for x in PIN_X:
        pins += np.exp(-(((u - (x + ARM_HALF) / (2 * ARM_HALF)) * 2 * ARM_HALF / 0.03) ** 2))
    ends = np.exp(-(u / 0.04) ** 2) + np.exp(-((1 - u) / 0.04) ** 2)
    lower = np.exp(-((v - cum[2]) / 0.06) ** 2) + np.exp(-((v - cum[5]) / 0.05) ** 2)
    r = np.clip(0.55 * ends + 0.5 * pins * (top_face + 0.4) + 0.35 * lower + 0.6 * (big * 0.5 + mid * 0.5 - 0.35), 0, 1)
    r = np.clip(r + 0.25 * fine * r, 0, 1)
    rust = lin((116, 64, 34)) * (0.85 + 0.3 * mid[..., None] * 0.5)
    col = zinc * (1 - r[..., None]) + rust * r[..., None]
    # rust run down the outer face (v grows downward there)
    drips = np.clip(fnoise(h, w, 1.5, 30, rng=rng), 0, None) * outer * np.clip((v - cum[3]) / (cum[4] - cum[3]), 0, 1)
    col = col * (1 - 0.4 * drips[..., None]) + rust * 0.4 * drips[..., None]
    # bird droppings on top
    dro = np.zeros((h, w), np.float32)
    nd = 26
    splat(dro, rng.integers(0, w, nd), rng.integers(0, int(cum[1] * h), nd), rng.uniform(2, 7, nd), 1.0, wrap=False, op='max')
    dro = np.clip(dro * (0.6 + 0.5 * fine), 0, 1) * top_face
    col = col * (1 - dro[..., None]) + lin((218, 214, 200)) * dro[..., None]
    height = 0.04 * mid + 0.03 * fine + r * 0.08 * fine + dro * 0.25
    metallic = np.clip(0.35 * (1 - r) - dro, 0, 1)
    smoothness = np.clip(0.34 * (1 - r) + 0.15 * r - 0.2 * dro, 0.05, 0.7)
    occ = cavity(height, 3, 1.5) * (1 - 0.25 * under)
    return col, height, metallic, occ, smoothness, 1.46, 0.74


def steel(rng):
    w, h = 1024, 256
    big = fnoise(h, w, 30, 30, rng=rng); mid = fnoise(h, w, 5, 5, rng=rng); fine = fnoise(h, w, 1.4, 1.4, rng=rng)
    r = np.clip(0.45 + 0.35 * big + 0.2 * mid, 0, 1)
    dark = lin((58, 48, 42)); orange = lin((108, 66, 42)); grey = lin((96, 96, 94))
    col = dark * (1 - r[..., None]) + orange * r[..., None]
    bare = (big + 0.5 * mid) < -1.1
    col = np.where(bare[..., None], grey, col) * (1 + 0.1 * fine)[..., None]
    height = 0.05 * mid + 0.06 * fine * r
    metallic = np.where(bare, 0.7, 0.05).astype(np.float32)
    smoothness = np.where(bare, 0.4, 0.15 + 0.05 * fine).astype(np.float32)
    return col, height, metallic, cavity(height, 3, 2.0), smoothness, 0.5, 0.5


def porcelain(rng):
    w, h = 512, 256
    prof = INSULATOR
    d = [math.dist(prof[i], prof[i + 1]) for i in range(len(prof) - 1)]
    cum = np.cumsum([0] + d) / sum(d)
    v = (np.arange(h)[:, None] + 0.5) / h
    v = np.broadcast_to(v, (h, w))
    # how much each part faces up (dirt settles there): the slope of the profile
    up = np.zeros_like(v)
    for i in range(len(prof) - 1):
        dr = prof[i + 1][0] - prof[i][0]; dz = prof[i + 1][1] - prof[i][1]
        facing = max(0.0, -dr / (math.hypot(dr, dz) + 1e-9))   # going inward as it rises: an upper surface
        up = np.where((v >= cum[i]) & (v < cum[i + 1]), facing, up)
    mid = fnoise(h, w, 8, 6, rng=rng); fine = fnoise(h, w, 1.5, 1.5, rng=rng)
    glaze = lin((228, 226, 216)) * (1 + 0.03 * mid)[..., None]
    dirt = np.clip(0.15 + 0.6 * up + 0.15 * mid, 0, 1)
    col = glaze * (1 - 0.45 * dirt[..., None]) + lin((112, 104, 92)) * 0.45 * dirt[..., None]
    # the inside of the foot dark
    col *= (1 - 0.6 * np.clip((cum[1] - v) / cum[1], 0, 1))[..., None]
    # the tie wire round the neck: dull aluminium
    neck = cum[NECK_RING]
    tie = np.exp(-((v - neck) / 0.035) ** 2) * (0.75 + 0.25 * np.sign(np.sin(np.arange(w)[None, :] * 0.9)))
    tie = np.clip(tie, 0, 1)
    col = col * (1 - tie[..., None]) + lin((150, 152, 150)) * (0.9 + 0.1 * fine[..., None]) * tie[..., None]
    height = 0.02 * fine + tie * 0.6
    metallic = 0.75 * tie
    smoothness = np.clip(0.82 - 0.5 * dirt - 0.3 * tie, 0.1, 0.9)
    return col, height, metallic, cavity(height, 3, 3.0), smoothness, 0.4, 0.45


def top_cap(rng):
    w, h = 512, 256
    big = fnoise(h, w, 25, 25, rng=rng); mid = fnoise(h, w, 4, 4, rng=rng); fine = fnoise(h, w, 1.4, 1.4, rng=rng)
    col = lin((112, 110, 104)) * (1 + 0.1 * big + 0.08 * mid)[..., None]
    moss = np.clip(big * 0.8 + 0.2, 0, 1)
    col = col * (1 - 0.5 * moss[..., None]) + lin((70, 78, 50)) * 0.5 * moss[..., None]
    dro = np.zeros((h, w), np.float32)
    splat(dro, rng.integers(0, w, 14), rng.integers(0, h, 14), rng.uniform(4, 12, 14), 1.0, op='max')
    dro = np.clip(dro * (0.6 + 0.4 * fine), 0, 1)
    col = col * (1 - dro[..., None]) + lin((210, 206, 192)) * dro[..., None]
    height = 0.3 * mid + 0.15 * fine
    return col, height, np.zeros((h, w), np.float32), cavity(height, 4, 0.8), np.full((h, w), 0.18, np.float32), 0.6, 0.6


def fracture(rng):
    w, h = 512, 512
    paste = lin((150, 146, 138))
    col = np.broadcast_to(paste, (h, w, 3)).copy() * (1 + 0.06 * fnoise(h, w, 3, 3, rng=rng))[..., None]
    height = np.zeros((h, w), np.float32) + 0.6 * fnoise(h, w, 2, 2, rng=rng)
    stones = [lin((168, 150, 142)), lin((190, 180, 160)), lin((84, 82, 80)), lin((150, 138, 120)), lin((120, 112, 104))]
    n = 700
    xs = rng.integers(0, w, n); ys = rng.integers(0, h, n); rs = rng.uniform(3, 18, n)
    order = np.argsort(rs)[::-1]
    yy, xx = np.mgrid[:h, :w]
    for i in order:
        x0, y0, r0 = xs[i], ys[i], rs[i]
        x1, x2, y1, y2 = max(0, int(x0 - r0)), min(w, int(x0 + r0) + 1), max(0, int(y0 - r0)), min(h, int(y0 + r0) + 1)
        d = np.hypot(xx[y1:y2, x1:x2] - x0, (yy[y1:y2, x1:x2] - y0) * rng.uniform(0.7, 1.3)) / r0
        inside = d < 1
        dome = np.sqrt(np.clip(1 - d ** 2, 0, 1)) * r0 * 0.5
        sub = height[y1:y2, x1:x2]
        top = inside & (dome > sub)
        sub[top] = dome[top]
        c = stones[rng.integers(0, len(stones))] * rng.uniform(0.85, 1.12)
        col[y1:y2, x1:x2][top] = c
    height *= 0.6
    return col, height, np.zeros((h, w), np.float32), cavity(height, 4, 0.25), np.full((h, w), 0.14, np.float32), 0.35, 0.35


def plain(h, w, rng):
    col = np.broadcast_to(lin((140, 138, 131)), (h, w, 3)).copy() * (1 + 0.05 * fnoise(h, w, 8, 8, rng=rng))[..., None]
    return col, np.zeros((h, w), np.float32), np.zeros((h, w), np.float32), np.ones((h, w), np.float32), np.full((h, w), 0.18, np.float32)


# ---------------------------------------------------------------------------------------------------------- the atlas

def main():
    package = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), '..', '..')
    out_dir = os.path.join(package, 'Textures', 'PowerPoles')
    os.makedirs(out_dir, exist_ok=True)
    albedo = np.zeros((N, N, 3), np.float32)
    normal = np.zeros((N, N, 3), np.float32); normal[..., 2] = 1
    mask = np.zeros((N, N, 4), np.float32)

    def put(x, y, col, nrm, met, occ, smo):
        h, w = col.shape[:2]
        albedo[y:y + h, x:x + w] = col
        normal[y:y + h, x:x + w] = nrm
        mask[y:y + h, x:x + w, 0] = met
        mask[y:y + h, x:x + w, 1] = occ
        mask[y:y + h, x:x + w, 2] = 0
        mask[y:y + h, x:x + w, 3] = smo

    # spare first, then the parts over it
    c, hgt, m, o, s = plain(N, N, RNG)
    put(0, 0, c, normal_from_height(hgt, 3, 3), m, o, s)
    for i, kind in enumerate(('weathered', 'aged', 'scarred')):
        c, hgt, m, o, s = shaft(kind, np.random.default_rng(100 + i))
        put(i * SW, 0, c, normal_from_height(hgt, PX_U, PX_V, 1.0), m, o, s)
        print('shaft', kind)
    for k, ((x, y), part) in enumerate((((1024, 1792), crossarm), ((1024, 1536), steel), ((1024, 1280), porcelain), ((1536, 1280), top_cap), ((1024, 768), fracture))):
        c, hgt, m, o, s, pxu, pxv = part(np.random.default_rng(200 + k))
        put(x, y, c, normal_from_height(hgt, pxu, pxv), m, o, s)
        print(part.__name__)

    flip = lambda a: a[::-1]
    Image.fromarray((srgb(flip(albedo)) * 255 + 0.5).astype(np.uint8), 'RGB').save(os.path.join(out_dir, 'power_pole_albedo.png'), optimize=True)
    Image.fromarray((np.clip(flip(normal) * 0.5 + 0.5, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGB').save(os.path.join(out_dir, 'power_pole_normal.png'), optimize=True)
    Image.fromarray((np.clip(flip(mask), 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA').save(os.path.join(out_dir, 'power_pole_mask.png'), optimize=True)
    print('written to', os.path.abspath(out_dir))


if __name__ == '__main__':
    main()
