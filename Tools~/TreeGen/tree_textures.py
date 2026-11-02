# Текстуры деревьев (Tools/TreeGen): процедурная кора (numpy, бесшовная) и атласы карточек листвы/хвои, отрендеренные
# из смоделированных листьев и побегов (Cycles, ортокамера сверху, эмиссия -> альбедо, нормаль камеры -> касательная
# нормаль карточки). Вывод — PNG: альбедо (sRGB, у карточек альфа), нормали (линейно, OpenGL), маска в соглашении
# NatureManufacture: R металличность 0, G AO, B высота, A гладкость.
import math
import os
import random

import bmesh
import bpy
import numpy as np
from mathutils import Euler, Matrix, Quaternion, Vector

import envgen_common as eg


# ------------------------------------------------------------------ бесшовный шум (numpy)

def periodic_value_noise(size, period, seed, octaves=4, persistence=0.5):
    """Бесшовный value noise size x size, базовый период period клеток."""
    rng = np.random.default_rng(seed)
    out = np.zeros((size, size), np.float32)
    amp = 1.0
    total = 0.0
    p = period
    for _ in range(octaves):
        grid = rng.random((p, p)).astype(np.float32)
        y = np.linspace(0, p, size, endpoint=False)
        x = np.linspace(0, p, size, endpoint=False)
        yi = np.floor(y).astype(int)
        xi = np.floor(x).astype(int)
        fy = (y - yi)[:, None]
        fx = (x - xi)[None, :]
        fy = fy * fy * (3 - 2 * fy)
        fx = fx * fx * (3 - 2 * fx)
        a = grid[yi % p][:, xi % p]
        b = grid[yi % p][:, (xi + 1) % p]
        c = grid[(yi + 1) % p][:, xi % p]
        d = grid[(yi + 1) % p][:, (xi + 1) % p]
        out += amp * ((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy)
        total += amp
        amp *= persistence
        p *= 2
    return out / total


def periodic_voronoi(size, cells_x, cells_y, seed, jitter=0.85):
    """Бесшовный Вороной: (F1, F2 - F1, id клетки) на сетке cells_x x cells_y (клетки можно вытянуть)."""
    rng = np.random.default_rng(seed)
    pts = (np.stack(np.meshgrid(np.arange(cells_x), np.arange(cells_y)), -1).astype(np.float32)
           + 0.5 + (rng.random((cells_y, cells_x, 2)).astype(np.float32) - 0.5) * jitter)
    ids = rng.random((cells_y, cells_x)).astype(np.float32)
    y, x = np.mgrid[0:size, 0:size].astype(np.float32)
    gx = x / size * cells_x
    gy = y / size * cells_y
    f1 = np.full((size, size), 1e9, np.float32)
    f2 = np.full((size, size), 1e9, np.float32)
    idm = np.zeros((size, size), np.float32)
    cx = np.floor(gx).astype(int)
    cy = np.floor(gy).astype(int)
    for oy in (-1, 0, 1):
        for ox in (-1, 0, 1):
            nx = cx + ox
            ny = cy + oy
            px = pts[ny % cells_y, nx % cells_x, 0] + (nx - nx % cells_x)
            py = pts[ny % cells_y, nx % cells_x, 1] + (ny - ny % cells_y)
            d = np.sqrt(((gx - px) * (size / cells_x)) ** 2 + ((gy - py) * (size / cells_y)) ** 2) / size
            closer = d < f1
            f2 = np.where(closer, f1, np.minimum(f2, d))
            idm = np.where(closer, ids[ny % cells_y, nx % cells_x], idm)
            f1 = np.where(closer, d, f1)
    return f1, f2 - f1, idm


def height_to_normal(height, strength):
    """Бесшовная нормаль (OpenGL, Y вверх по картинке) из высоты 0..1."""
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * 0.5 * strength
    dy = (np.roll(height, 1, 0) - np.roll(height, -1, 0)) * 0.5 * strength
    n = np.stack([-dx, -dy, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def ao_from_height(height, radius=6):
    blur = height.copy()
    for _ in range(radius):
        blur = (blur + np.roll(blur, 1, 0) + np.roll(blur, -1, 0) + np.roll(blur, 1, 1) + np.roll(blur, -1, 1)) / 5
    return np.clip(1.0 - np.maximum(blur - height, 0) * 4.0, 0.35, 1.0)


def colorize(height, stops):
    """Цвет по высоте: stops — [(высота 0..1, '#hex')], линейные значения."""
    hs = np.array([s[0] for s in stops], np.float32)
    cs = np.array([eg.hex_rgb(s[1]) for s in stops], np.float32)
    out = np.zeros(height.shape + (3,), np.float32)
    for k in range(3):
        out[..., k] = np.interp(height, hs, cs[:, k])
    return out


def save_bark(out_dir, prefix, albedo, height, normal_strength, smoothness, size):
    normal = height_to_normal(height, normal_strength)
    ao = ao_from_height(height)
    mask = np.stack([np.zeros_like(height), ao, height, np.full_like(height, smoothness)], -1)
    eg.save_png(albedo * ao[..., None] ** 0.5, os.path.join(out_dir, prefix + '_albedo.png'))
    eg.save_png(normal, os.path.join(out_dir, prefix + '_normal.png'), linear=True)
    eg.save_png(mask, os.path.join(out_dir, prefix + '_mask.png'), linear=True)
    return [prefix + '_albedo.png', prefix + '_normal.png', prefix + '_mask.png']


# ------------------------------------------------------------------ кора

def bark_pine_lower(out_dir, size=1024, seed=11):
    """Нижняя кора сосны: толстые серо-бурые пластины, вытянутые вдоль ствола, между ними глубокие тёмные трещины
    с рыжиной в глубине. Текстура покрывает ~0.5 м по окружности и ~0.5 м по длине (повтор задаёт генератор)."""
    f1, edge, cid = periodic_voronoi(size, 5, 9, seed, jitter=0.9)
    warp = periodic_value_noise(size, 4, seed + 1, 4)
    crack = np.clip(edge * size / 9.0 + (warp - 0.5) * 0.6, 0, 1)
    plates = np.clip(crack * 2.2, 0, 1) ** 0.6
    fine = periodic_value_noise(size, 16, seed + 2, 4)
    flakes = periodic_value_noise(size, 32, seed + 3, 3)
    height = np.clip(plates * 0.8 + fine * 0.15 + flakes * 0.08 + cid * 0.05, 0, 1)
    albedo = colorize(height, [(0.0, '#2a1d14'), (0.18, '#5b3a24'), (0.35, '#5e5248'), (0.7, '#7a6e62'), (1.0, '#968a7c')])
    albedo *= (0.85 + 0.3 * cid[..., None]) * (0.92 + 0.16 * flakes[..., None])
    return save_bark(out_dir, 'bark_pine_lower', albedo, height, 9.0, 0.08, size)


def bark_pine_upper(out_dir, size=1024, seed=21):
    """Верхняя кора сосны: тонкие рыже-оранжевые отслаивающиеся чешуйки поперёк ствола."""
    stripes = periodic_value_noise(size, 6, seed, 5)
    across = periodic_voronoi(size, 3, 22, seed + 1, jitter=0.8)
    peel = np.clip(across[1] * size / 22.0 * 1.6, 0, 1)
    fine = periodic_value_noise(size, 24, seed + 2, 3)
    height = np.clip(0.55 * peel + 0.3 * stripes + 0.15 * fine, 0, 1)
    albedo = colorize(height, [(0.0, '#6a3218'), (0.3, '#a0552c'), (0.6, '#c27a45'), (0.85, '#d39660'), (1.0, '#ddb08a')])
    albedo *= (0.9 + 0.2 * fine[..., None])
    return save_bark(out_dir, 'bark_pine_upper', albedo, height, 4.0, 0.12, size)


def bark_oak(out_dir, size=1024, seed=31):
    """Кора дуба: глубокие продольные борозды, между ними узкие ломаные гребни с поперечными трещинами; серо-бурая."""
    ridges_v = periodic_voronoi(size, 7, 4, seed, jitter=0.95)
    warp = periodic_value_noise(size, 3, seed + 1, 4)
    ridge = np.clip(ridges_v[1] * size / 7.0 * 1.8 + (warp - 0.5) * 0.5, 0, 1) ** 0.7
    cross = periodic_voronoi(size, 10, 26, seed + 2, jitter=0.9)
    breaks = np.clip(cross[1] * size / 26.0 * 3.0, 0, 1)
    fine = periodic_value_noise(size, 20, seed + 3, 4)
    lichen = np.clip((periodic_value_noise(size, 5, seed + 4, 4) - 0.62) * 6, 0, 1)
    height = np.clip(ridge * (0.75 + 0.25 * breaks) * 0.85 + fine * 0.15, 0, 1)
    albedo = colorize(height, [(0.0, '#1c1712'), (0.25, '#3a3129'), (0.5, '#5a5048'), (0.8, '#76695e'), (1.0, '#8c8075')])
    albedo = albedo * (1 - lichen[..., None] * 0.5) + np.array(eg.hex_rgb('#6f7a55'), np.float32) * lichen[..., None] * 0.5 * height[..., None]
    return save_bark(out_dir, 'bark_oak', albedo, height, 11.0, 0.06, size)


# ------------------------------------------------------------------ атласы карточек

def _emission(name, color):
    return eg.emission_material(name, lambda nt: (lambda n: (setattr(n.outputs[0], 'default_value', (*color, 1.0)), n.outputs[0])[1])(nt.nodes.new('ShaderNodeRGB')))


def _vertex_color_emission(name):
    """Эмиссия цветом вершин (атрибут 'Col', линейный)."""
    def build(nt):
        a = nt.nodes.new('ShaderNodeVertexColor')
        a.layer_name = 'Col'
        return a.outputs['Color']
    return eg.emission_material(name, build)


def render_atlas(objects_per_cell, grid, cell_px, cell_size_m, out_dir, prefix, samples=32):
    """Каждую ячейку (список объектов, стоящих в (0,0) и смотрящих вверх по +Y картинки) рендерит сверху ортокамерой
    в cell_px, собирает атлас grid=(столбцы, строки). Карточка в игре: +Y картинки = от веточки наружу."""
    scene = bpy.context.scene
    cols, rows = grid
    eg.setup_card_render(scene, cell_px, cell_px, samples=samples)
    cam = eg.ortho_camera(scene, (0, cell_size_m * 0.5, 50), (0, 0, 0), cell_size_m, name='atlas_cam')
    normal_mat = eg.view_normal_material('atlas_normal')
    tmp = os.path.join(out_dir, '_atlas_tmp')
    os.makedirs(tmp, exist_ok=True)
    al = np.zeros((rows * cell_px, cols * cell_px, 4), np.float32)
    nm = np.zeros((rows * cell_px, cols * cell_px, 4), np.float32)
    all_objs = [o for objs in objects_per_cell for o in objs]
    for k, objs in enumerate(objects_per_cell):
        for o in all_objs:
            o.hide_render = o not in objs
        pa = os.path.join(tmp, f'{prefix}_a{k}.png')
        pn = os.path.join(tmp, f'{prefix}_n{k}.png')
        scene.render.filepath = pa
        bpy.ops.render.render(write_still=True)
        eg.render_with_override(scene, objs, normal_mat, pn)
        a = eg.srgb_png_to_linear_values(pa)
        n = eg.srgb_png_to_linear_values(pn)
        ci, cj = k % cols, k // cols
        al[cj * cell_px:(cj + 1) * cell_px, ci * cell_px:(ci + 1) * cell_px] = a
        nm[cj * cell_px:(cj + 1) * cell_px, ci * cell_px:(ci + 1) * cell_px, :3] = n[..., :3]
        nm[cj * cell_px:(cj + 1) * cell_px, ci * cell_px:(ci + 1) * cell_px, 3] = a[..., 3]
    for o in all_objs:
        o.hide_render = False
    al = eg.dilate_rgb(al, 10)
    nm[..., :3] = np.where(nm[..., 3:4] > 0.01, nm[..., :3], np.array([0.5, 0.5, 1.0], np.float32))
    nm = eg.dilate_rgb(nm, 4)
    mask = np.stack([np.zeros(al.shape[:2]), np.clip(0.55 + 0.45 * al[..., 3], 0, 1),
                     np.full(al.shape[:2], 0.5), np.full(al.shape[:2], 0.35)], -1).astype(np.float32)
    names = [prefix + '_albedo.png', prefix + '_normal.png', prefix + '_mask.png']
    eg.save_png(al, os.path.join(out_dir, names[0]))
    eg.save_png(nm[..., :3], os.path.join(out_dir, names[1]), linear=True)
    eg.save_png(mask, os.path.join(out_dir, names[2]), linear=True)
    bpy.data.objects.remove(cam)
    return names


def oak_leaf_mesh(mb, rng, base, direction, up, length, color_a, color_b, material=0):
    """Лист дуба черешчатого: обратнояйцевидный, 5-6 округлых лопастей с каждой стороны, ушки у основания, черешок
    короткий; слегка сложен по средней жилке. Цвет — по вершинам (центр светлее у жилки)."""
    side = direction.cross(up).normalized()
    n_lobes = rng.choice([5, 6])
    outline = []
    steps = 60
    for i in range(steps + 1):
        t = i / steps  # 0 основание .. 1 верхушка
        env = math.sin(math.pi * min(1, t * 1.05)) ** 0.8 * (0.35 + 0.65 * t ** 0.6) * (1 - 0.35 * t ** 4)
        lobe = 0.62 + 0.38 * abs(math.sin(t * n_lobes * math.pi))
        aur = 0.25 * math.exp(-((t - 0.04) / 0.04) ** 2)  # ушки
        w = (env * lobe + aur) * 0.32
        outline.append((t, w))
    mid_idx = []
    for t, w in outline:
        p = base + direction * (t * length)
        fold = abs(w) * length * 0.18
        c = color_a.lerp(color_b, rng.random() * 0.3)
        l = mb.vert(p - side * w * length + up * fold)
        m = mb.vert(p)
        r = mb.vert(p + side * w * length + up * fold)
        mid_idx.append((l, m, r, c))
    for i in range(len(mid_idx) - 1):
        l0, m0, r0, c0 = mid_idx[i]
        l1, m1, r1, c1 = mid_idx[i + 1]
        vein = tuple(x * 1.12 for x in c0)
        edge = tuple(x * 0.92 for x in c0)
        mb.face([l0, m0, m1, l1], material, None, None, [(*edge, 1), (*vein, 1), (*vein, 1), (*edge, 1)])
        mb.face([m0, r0, r1, m1], material, None, None, [(*vein, 1), (*edge, 1), (*edge, 1), (*vein, 1)])


def twig_mesh(mb, start, end, radius, color, material=0, sides=5):
    d = (end - start)
    axis = d.normalized()
    nx = axis.orthogonal().normalized()
    ny = axis.cross(nx).normalized()
    ring0, ring1 = [], []
    for s in range(sides):
        a = s / sides * math.tau
        off = nx * math.cos(a) + ny * math.sin(a)
        ring0.append(mb.vert(start + off * radius))
        ring1.append(mb.vert(end + off * radius * 0.6))
    c = (*color, 1)
    for s in range(sides):
        mb.face([ring0[s], ring0[(s + 1) % sides], ring1[(s + 1) % sides], ring1[s]], material, None, None, [c] * 4)


def oak_leaf_atlas(out_dir, seed=5, cell_px=512, cell_m=0.55, palette=None):
    """Атлас 2x2 кистей листьев дуба: веточка снизу ячейки вверх, 7-12 листьев 7-14 см пучками у концов побегов
    (у дуба листья скучены на концах побегов)."""
    palette = palette or dict(top=('#3c5a22', '#4f6e2b'), light=('#5b7a33', '#6f8c3f'), twig='#4a3b2a')
    rng = random.Random(seed)
    eg.reset_scene()
    mat = _vertex_color_emission('leaf_vc')
    cells = []
    for k in range(4):
        mb = eg.MeshBuilder()
        twig_c = Vector(eg.hex_rgb(palette['twig']))
        base = Vector((rng.uniform(-0.02, 0.02), 0.02, 0))
        tip = Vector((rng.uniform(-0.08, 0.08), cell_m * 0.62, 0))
        twig_mesh(mb, base, tip, 0.006, tuple(twig_c))
        # 2-3 побега, на каждом пучок листьев
        shoots = rng.choice([2, 3])
        for s in range(shoots):
            t0 = rng.uniform(0.35, 0.85)
            p0 = base.lerp(tip, t0)
            ang = rng.uniform(-0.9, 0.9)
            sd = Vector((math.sin(ang), math.cos(ang), 0))
            p1 = p0 + sd * rng.uniform(0.08, 0.16)
            twig_mesh(mb, p0, p1, 0.004, tuple(twig_c))
            for j in range(rng.randint(3, 5)):
                a = ang + rng.uniform(-1.3, 1.3)
                d = Vector((math.sin(a), math.cos(a), 0)).normalized()
                up = Vector((0, 0, 1))
                up.rotate(Quaternion(d, rng.uniform(-0.5, 0.5)))
                length = rng.uniform(0.07, 0.14)
                ca = Vector(eg.hex_rgb(rng.choice(palette['top'])))
                cb = Vector(eg.hex_rgb(rng.choice(palette['light'])))
                oak_leaf_mesh(mb, rng, p1 + Vector((0, 0, rng.uniform(-0.01, 0.01))), d, up, length, ca, cb)
        for j in range(rng.randint(3, 5)):
            a = rng.uniform(-1.0, 1.0)
            d = Vector((math.sin(a), math.cos(a), 0)).normalized()
            ca = Vector(eg.hex_rgb(rng.choice(palette['top'])))
            cb = Vector(eg.hex_rgb(rng.choice(palette['light'])))
            oak_leaf_mesh(mb, rng, tip, d, Vector((0, 0, 1)), rng.uniform(0.08, 0.14), ca, cb)
        obj = mb.to_object(f'oak_cell_{k}', [mat])
        cells.append([obj])
    return render_atlas(cells, (2, 2), cell_px, cell_m, out_dir, 'oak_leaves')


def pine_needle_atlas(out_dir, seed=7, cell_px=512, cell_m=0.5, palette=None):
    """Атлас 2x2 концов ветвей сосны: побег с мутовкой 2-4 боковых побегов, на последних 25-40 см каждого — щётка хвои:
    иголки 4-7 см парами, торчат вперёд под 35-65° к побегу; сине-зелёные, у основания побега хвоя редеет."""
    palette = palette or dict(needle=('#3e5a3c', '#4a6646', '#56704c', '#35503a'), twig='#6b4a2e')
    rng = random.Random(seed)
    eg.reset_scene()
    mat = _vertex_color_emission('needle_vc')
    cells = []

    def needles(mb, start, end, density):
        axis = (end - start)
        length = axis.length
        axis.normalize()
        count = int(length * density)
        for i in range(count):
            t = rng.uniform(0.0, 1.0)
            p = start + axis * (t * length)
            for _pair in range(2):
                a = rng.uniform(0, math.tau)
                spread = math.radians(rng.uniform(35, 65)) * (0.7 + 0.5 * (1 - t))
                side = axis.orthogonal().normalized()
                side.rotate(Quaternion(axis, a))
                d = (axis * math.cos(spread) + side * math.sin(spread)).normalized()
                nl = rng.uniform(0.04, 0.07)
                w = 0.0012
                c = Vector(eg.hex_rgb(rng.choice(palette['needle']))) * rng.uniform(0.85, 1.15)
                tip = p + d * nl
                perp = d.cross(Vector((0, 0, 1))).normalized() * w
                v0 = mb.vert(p - perp)
                v1 = mb.vert(p + perp)
                v2 = mb.vert(tip)
                mb.face([v0, v1, v2], 0, None, None, [(*(c * 0.8), 1), (*(c * 0.8), 1), (*(c * 1.15), 1)])

    for k in range(4):
        mb = eg.MeshBuilder()
        twig_c = tuple(Vector(eg.hex_rgb(palette['twig'])))
        base = Vector((0, 0.01, 0))
        tip = Vector((rng.uniform(-0.04, 0.04), cell_m * 0.92, 0))
        twig_mesh(mb, base, tip, 0.007, twig_c)
        needles(mb, base.lerp(tip, 0.45), tip, 260)
        for s in range(rng.randint(2, 4)):
            t0 = rng.uniform(0.25, 0.55)
            p0 = base.lerp(tip, t0)
            ang = rng.choice([-1, 1]) * rng.uniform(0.5, 1.0)
            d = Vector((math.sin(ang), math.cos(ang), 0))
            p1 = p0 + d * rng.uniform(0.16, 0.26)
            twig_mesh(mb, p0, p1, 0.005, twig_c)
            needles(mb, p0.lerp(p1, 0.25), p1, 240)
        obj = mb.to_object(f'pine_cell_{k}', [mat])
        cells.append([obj])
    return render_atlas(cells, (2, 2), cell_px, cell_m, out_dir, 'pine_needles')
