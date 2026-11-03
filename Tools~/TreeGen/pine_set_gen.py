# Сосны из готового набора Vegetation_Set_Pine.blend (4 дерева на общем атласе растительности 4K: береза, ель, трава,
# сосна; 17-29 м, 1-4 тыс. треугольников, один LOD) для Nature Renderer: каждое дерево — отдельный ассет для
# VegetationImporter (manifest.json + FBX LOD0..LOD2 + крест + PNG), как у сосен pine_gen.py.
#
# Запуск (сцена набора открывается без его скриптов — в нём есть test.py):
#   blender.exe -b --factory-startup --disable-autoexec <Vegetation_Set_Pine.blend> --python pine_set_gen.py -- <папка вывода>
#
# Из набора: ствол (одна часть, кора plant_pine_bark — бесшовная, развёрнута плиткой) и карточки веток (сотни частей по
# 10 вершин: ветки хвои и сухие сучья) — у всех сосен в одном окне атласа 1024 x 1024 (левый нижний квадрант ниже
# середины), справа к нему вплотную ель. Это окно вырезается в свой атлас без пересэмплирования: альбедо — цвет атласа,
# альфа — его маска; UV карточек пересчитываются в окно. Нормали карт набора нет: у хвои плоская (свет даёт нормаль
# вершин), у коры — из яркости (высота), маски NM (R металличность 0, G AO, B высота / 0.5, A гладкость) — так же.
# Положение: трансформы применены (у D масштаб 1.389), комель на оси в начале координат, земля сцены — z 0 (ствол уходит
# в землю на 0.34 м). Ветер NM — в альфе цвета вершин (как у pine_gen: ствол 0 до низа кроны и до 0.2 у вершины, ветки —
# ещё 0.35 к краю кроны, сухие сучья 0.1); UV2 — (0, z/H).
# LOD1/LOD2: часть карточек хвои (0.5 / 0.25, крупные — чаще) и сухих сучьев (0.35 / 0) в каждой ячейке кроны 1.2 / 2 м
# (не меньше одной), оставшиеся увеличены от точки у ствола — масштаб подогнан по силуэту LOD0 сбоку и сверху (как у
# pine_gen). Крест — из LOD0 функциями pine_gen,
# цвет подогнан под LOD2 рендером.
import json
import math
import os
import random
import shutil
import sys

import bpy
import numpy as np
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402
import pine_gen as pg  # noqa: E402

GROUP = 'PineSet'
TREES = {'tree_pine_A': 'pine_set_a', 'tree_pine_B': 'pine_set_b', 'tree_pine_C': 'pine_set_c', 'tree_pine_D': 'pine_set_d'}
BARK_IMAGE, COLOR_IMAGE, MASK_IMAGE = 'plant_pine_bark.png', 'ALL_VEGETATION_color.png', 'ALL_VEGETATION_mask.png'
# окно сосны в атласе 4096: x 0..1024, строки сверху 2044..3068
WINDOW = (0, 2044, 1024)
DRY_V = 0.405                    # карточки выше этого v атласа набора — сухие сучья (верхние ряды окна)
WIND = pg.WIND
M_BARK, M_LEAVES = pg.M_LOWER, pg.M_LEAVES
KEEP = {1: (0.5, 0.35), 2: (0.25, 0.0)}   # LOD: доля карточек хвои, сухих сучьев
CELL = {1: 1.2, 2: 2.0}                   # LOD: ячейка прореживания, м
PREFIX = 'pine_set'


def image_pixels(name):
    """Пиксели упакованной картинки (H, W, 4), строки сверху вниз, как записаны (у sRGB — sRGB)."""
    img = bpy.data.images[name]
    w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    return px.reshape(h, w, 4)[::-1].copy()


def to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def wrap_blur(a, r):
    """Блюр квадратом 2r+1 с переносом через край (кора бесшовная)."""
    out = a.copy()
    for axis in (0, 1):
        acc = np.zeros_like(out)
        for d in range(-r, r + 1):
            acc += np.roll(out, d, axis)
        out = acc / (2 * r + 1)
    return out


def make_textures(folder):
    """Атлас хвои из окна набора и кора с нормалью и маской из яркости. Возвращает имена файлов."""
    x0, y0, n = WINDOW
    color = image_pixels(COLOR_IMAGE)[y0:y0 + n, x0:x0 + n, :3]
    alpha = image_pixels(MASK_IMAGE)[y0:y0 + n, x0:x0 + n, 0].copy()
    # край окна прозрачный: UV карточек за ним (до 4 пикселей) берут край по Clamp
    alpha[:, -2:] = 0
    alpha[:2] = 0
    alpha[-2:] = 0
    alpha[:, :2] = 0
    # цвет — только из плотных пикселей: полупрозрачный край маски смешан со светлым фоном набора и растекался каймой
    known = alpha > 0.8
    lin = to_linear(color)
    mean = lin[known].mean(0)
    al = eg.dilate_rgb(np.concatenate([lin, known[..., None].astype(np.float32)], -1), 16)
    # дальше 16 пикселей от карточек — средний цвет хвои
    reach = known.copy()
    for _ in range(16):
        reach = reach | np.roll(reach, 1, 0) | np.roll(reach, -1, 0) | np.roll(reach, 1, 1) | np.roll(reach, -1, 1)
    al[..., :3] = np.where(known[..., None], lin, np.where(reach[..., None], al[..., :3], mean))
    al[..., 3] = alpha
    srgb = color
    brown = np.clip((srgb[..., 0] - srgb[..., 1]) * 8.0, 0, 1)
    smooth = 0.30 - 0.15 * brown
    mask = np.stack([np.zeros_like(alpha), np.ones_like(alpha), np.full_like(alpha, 0.5), smooth], -1)
    normal = np.zeros((n, n, 3), np.float32) + np.array([0.5, 0.5, 1.0], np.float32)
    names = {}
    names['needles'] = [f'{PREFIX}_needles_albedo.png', f'{PREFIX}_needles_normal.png', f'{PREFIX}_needles_mask.png']
    eg.save_png(al, os.path.join(folder, names['needles'][0]))
    eg.save_png(normal, os.path.join(folder, names['needles'][1]), linear=True)
    eg.save_png(mask, os.path.join(folder, names['needles'][2]), linear=True)
    # кора: высота — яркость (пластины светлее борозд), без крупных пятен (их снимает вычитание размытого)
    bark = image_pixels(BARK_IMAGE)[..., :3]
    lum = to_linear(bark) @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    h = wrap_blur(lum, 1) - wrap_blur(lum, 24) * 0.7
    lo, hi = np.percentile(h, (1, 99))
    h = np.clip((h - lo) / max(hi - lo, 1e-4), 0, 1)
    hs = wrap_blur(h, 1)
    gx = (np.roll(hs, -1, 1) - np.roll(hs, 1, 1)) * 0.5
    gv = (np.roll(hs, 1, 0) - np.roll(hs, -1, 0)) * 0.5      # вдоль +v (строки сверху вниз)
    strength = 5.0
    nrm = np.stack([-gx * strength, -gv * strength, np.ones_like(hs)], -1)
    nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
    ao = 0.45 + 0.55 * np.clip(wrap_blur(h, 4) * 1.4, 0, 1)
    bmask = np.stack([np.zeros_like(h), ao, h, 0.08 + 0.12 * h], -1)
    names['bark'] = [f'{PREFIX}_bark_albedo.png', f'{PREFIX}_bark_normal.png', f'{PREFIX}_bark_mask.png']
    eg.save_png(to_linear(bark), os.path.join(folder, names['bark'][0]))
    eg.save_png(nrm * 0.5 + 0.5, os.path.join(folder, names['bark'][1]), linear=True)
    eg.save_png(bmask, os.path.join(folder, names['bark'][2]), linear=True)
    return names, dict(needles_linear_mean=[round(float(x), 4) for x in mean],
                       bark_linear_mean=[round(float(x), 4) for x in to_linear(bark).reshape(-1, 3).mean(0)])


def read_tree(obj):
    """Меш в мире: вершины, грани (вершины, материал 0 кора / 1 хвоя), на углах — нормаль и UV; части (связность)."""
    me = obj.data
    M = obj.matrix_world
    N = M.to_3x3().inverted().transposed()
    verts = np.array([tuple(M @ v.co) for v in me.vertices], np.float64)
    cn = me.corner_normals
    uv = me.uv_layers['UVMap'].data
    faces = []
    for p in me.polygons:
        loops = range(p.loop_start, p.loop_start + p.loop_total)
        idx = [me.loops[i].vertex_index for i in loops]
        nrm = [tuple((N @ Vector(cn[i].vector)).normalized()) for i in loops]
        uvs = [tuple(uv[i].uv) for i in loops]
        faces.append(dict(v=idx, mat=p.material_index, n=nrm, uv=uvs))
    # части: объединение по вершинам граней
    parent = list(range(len(verts)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for f in faces:
        r0 = find(f['v'][0])
        for i in f['v'][1:]:
            r = find(i)
            if r != r0:
                parent[r] = r0
    parts = {}
    for fi, f in enumerate(faces):
        parts.setdefault(find(f['v'][0]), []).append(fi)
    return verts, faces, list(parts.values())


class TreeInfo:
    """То, что функциям креста pine_gen нужно от дерева: высота, низ кроны, ось ствола, центр кроны."""

    def __init__(self, H, zcb, axis, crown_c):
        self.H, self.zcb, self.axis, self.crown_c = H, zcb, axis, crown_c

    def center(self, z):
        zs = [a[0] for a in self.axis]
        x = float(np.interp(z, zs, [a[1] for a in self.axis]))
        y = float(np.interp(z, zs, [a[2] for a in self.axis]))
        return Vector((x, y, z))


def trunk_axis(verts, trunk_v):
    p = verts[trunk_v]
    z0, z1 = p[:, 2].min(), p[:, 2].max()
    axis = []
    for z in np.arange(z0 + 0.25, z1, 0.5):
        sel = np.abs(p[:, 2] - z) < 0.4
        if sel.sum() >= 3:
            c = p[sel, :2].mean(0)
            axis.append((float(z), float(c[0]), float(c[1])))
    return axis


def prepare(obj_name):
    """Дерево набора -> данные с комлем в начале координат, ось, части-карточки."""
    obj = bpy.data.objects[obj_name]
    verts, faces, parts = read_tree(obj)
    bark_parts = [pt for pt in parts if faces[pt[0]]['mat'] == 0]
    trunk = max(bark_parts, key=len)
    trunk_v = sorted({i for fi in trunk for i in faces[fi]['v']})
    axis = trunk_axis(verts, trunk_v)
    base = next(a for a in axis if a[0] >= 0.0)
    verts[:, 0] -= base[1]
    verts[:, 1] -= base[2]
    axis = [(z, x - base[1], y - base[2]) for z, x, y in axis]
    cards = []
    for pt in parts:
        if faces[pt[0]]['mat'] != 1:
            continue
        vi = sorted({i for fi in pt for i in faces[fi]['v']})
        v_mean = float(np.mean([uv[1] for fi in pt for uv in faces[fi]['uv']]))
        area = 0.0
        for fi in pt:
            q = verts[faces[fi]['v']]
            for k in range(1, len(q) - 1):
                area += 0.5 * np.linalg.norm(np.cross(q[k] - q[0], q[k + 1] - q[0]))
        cards.append(dict(faces=pt, verts=vi, kind='dry' if v_mean > DRY_V else 'spray', area=area,
                          centroid=verts[vi].mean(0)))
    spray_v = np.array(sorted({i for c in cards if c['kind'] == 'spray' for i in c['verts']}))
    H = float(verts[:, 2].max())
    zcb = float(np.percentile(verts[spray_v, 2], 8))
    info = TreeInfo(H, zcb, axis, Vector(verts[spray_v].mean(0)))

    def radial(p):
        c = info.center(p[2])
        return math.hypot(p[0] - c.x, p[1] - c.y)

    R = float(np.percentile([radial(verts[i]) for i in spray_v], 95))
    # радиус ствола на 1.3 м: кольца ствола редкие — прямая по вершинам от 0.2 до 4 м
    low = [(verts[i][2], radial(verts[i])) for i in trunk_v if 0.2 < verts[i][2] < 4.0]
    info.R = R
    info.trunk_radius = float(np.polyval(np.polyfit([z for z, _ in low], [r for _, r in low], 1), 1.3)) if len(low) > 3 else 0.2
    return dict(verts=verts, faces=faces, trunk=trunk, bark_parts=bark_parts, cards=cards, info=info, radial=radial)


def wind_trunk(info, z):
    return 0.2 * max(0.0, (z - info.zcb) / max(info.H - info.zcb, 1.0)) ** 2


def build_lod(src, lod, k=1.0, seed=0):
    """LOD: кора целиком, карточки — все (LOD0) или часть с масштабом k от точки у ствола."""
    verts, faces, info = src['verts'], src['faces'], src['info']
    keep_faces = [fi for pt in src['bark_parts'] for fi in pt]
    moved = {}
    rng = random.Random(1000 + seed * 17 + lod)
    kinds = {'spray': [c for c in src['cards'] if c['kind'] == 'spray'], 'dry': [c for c in src['cards'] if c['kind'] == 'dry']}
    card_kind = {}
    for kind, cards in kinds.items():
        if lod == 0:
            kept = cards
        else:
            # равномерно по кроне: в каждой ячейке своя доля (крупные — чаще), хотя бы одна карточка — иначе редкие
            # ветки вершины выпадали целиком и верх кроны оголялся
            frac = KEEP[lod][0 if kind == 'spray' else 1]
            kept = []
            if frac > 0:
                cells = {}
                for c in cards:
                    cells.setdefault(tuple(np.floor(c['centroid'] / CELL[lod]).astype(int)), []).append(c)
                for key in sorted(cells):
                    cell = sorted(cells[key], key=lambda c: -c['area'] * rng.uniform(0.5, 1.5))
                    kept.extend(cell[:max(1, int(round(len(cell) * frac)))])
        for c in kept:
            keep_faces.extend(c['faces'])
            for i in c['verts']:
                card_kind[i] = kind
            if lod > 0 and kind == 'spray':
                p = verts[c['verts']]
                ax = info.center(float(c['centroid'][2]))
                d = np.hypot(p[:, 0] - ax.x, p[:, 1] - ax.y)
                anchor = 0.7 * p[int(np.argmin(d))] + 0.3 * c['centroid']
                for i in c['verts']:
                    moved[i] = anchor + (verts[i] - anchor) * k
    mesh = pg.PMesh()
    remap = {}
    for fi in keep_faces:
        for i in faces[fi]['v']:
            if i not in remap:
                remap[i] = mesh.vert(tuple(moved.get(i, verts[i])))
    x0, y0, n = WINDOW
    for fi in keep_faces:
        f = faces[fi]
        idx = [remap[i] for i in f['v']]
        pos = [mesh.v[j] for j in idx]
        if f['mat'] == 0:
            col = [(0, 0, 0, wind_trunk(info, p[2])) for p in pos]
            mesh.face(idx, M_BARK, f['uv'], col, f['n'], [(0, p[2] / info.H) for p in pos])
        else:
            col = []
            for i, p in zip(f['v'], pos):
                r = min(1.0, src['radial'](p) / max(info.R, 0.5))
                w = wind_trunk(info, p[2]) + (0.35 if card_kind[i] == 'spray' else 0.1) * r ** 1.5
                w = min(0.85, w)
                col.append((w, w, w, w))
            uvs = [((u * 4096 - x0) / n, (v * 4096 - (4096 - y0 - n)) / n) for u, v in f['uv']]
            mesh.face(idx, M_LEAVES, uvs, col, f['n'], [(0, p[2] / info.H) for p in pos])
    return mesh


def build_asset(src, name, out_root, tex, report):
    folder = os.path.join(out_root, name)
    os.makedirs(folder, exist_ok=True)
    info = src['info']
    for files in tex.values():
        for f in files:
            shutil.copy(os.path.join(out_root, '_tex', f), os.path.join(folder, f))
    T = lambda f: os.path.join(folder, f)  # noqa: E731
    prev_mats = [pg.bark_material('bark', T(tex['bark'][0]), T(tex['bark'][1])), None,
                 pg.card_material('needles', T(tex['needles'][0]), 0.5)]
    pass_mats = [pg.pass_material('pass_bark', T(tex['bark'][0])), None,
                 pg.pass_material('pass_needles', T(tex['needles'][0]), 0.5)]
    ids = {M_BARK: 'bark', M_LEAVES: 'needles'}
    used = [M_BARK, M_LEAVES]
    scene = bpy.context.scene
    pg.card_scene(scene, 4)
    m0 = build_lod(src, 0)
    o0 = m0.to_object(f'{name}_LOD0', prev_mats, used)
    pg.add_nrm_attr(o0)
    vs0 = np.array([v.co[:] for v in o0.data.vertices])
    S, Sw, xc, yc = pg.ortho_views(vs0)
    crown = vs0[vs0[:, 2] > info.zcb - 0.5]
    tsize = max(np.ptp(crown[:, 0]), np.ptp(crown[:, 1])) * 1.1
    top_box = (0.5 * (crown[:, 0].min() + crown[:, 0].max()), 0.5 * (crown[:, 1].min() + crown[:, 1].max()), tsize)
    tmp = os.path.join(folder, '_sil_tmp')
    os.makedirs(tmp, exist_ok=True)
    pm = [pass_mats[m] for m in used]
    sil0 = pg.silhouette_cover(scene, o0, pm, S, Sw, xc, yc, top_box, tmp, 'l0')
    for s, m in zip(o0.material_slots, [prev_mats[m] for m in used]):
        s.material = m
    objs, meshes = [o0], [m0]
    lods = [(f'{name}_LOD0.fbx', pg.LOD_SCREEN[0], [ids[m] for m in used], True)]
    rep = dict(name=name, height=round(info.H, 2), crown_base=round(info.zcb, 2), crown_radius=round(info.R, 2),
               trunk_radius=round(info.trunk_radius, 3), sprays=sum(c['kind'] == 'spray' for c in src['cards']),
               dry=sum(c['kind'] == 'dry' for c in src['cards']), lods=[dict(lod=0, tris=m0.tris, sil=sil0)])
    o0.hide_render = True
    for lod in (1, 2):
        k = 1.0 / math.sqrt(KEEP[lod][0]) * 0.85
        best = None
        for it in range(5):
            mesh = build_lod(src, lod, k)
            ob = mesh.to_object(f'_tmp{lod}_{it}', prev_mats, used)
            pg.add_nrm_attr(ob)
            sil = pg.silhouette_cover(scene, ob, pm, S, Sw, xc, yc, top_box, tmp, f'l{lod}{it}')
            ratio = 0.5 * (sil0[0] / max(sil[0], 1e-3) + sil0[1] / max(sil[1], 1e-3))
            err = abs(ratio - 1)
            bpy.data.objects.remove(ob)
            if best is None or err < best[0]:
                best = (err, k, sil)
            if err < 0.04:
                break
            k = pg.clamp(k * ratio ** 0.65, 0.8, 3.0)
        k = best[1]
        mesh = build_lod(src, lod, k)
        ob = mesh.to_object(f'{name}_LOD{lod}', prev_mats, used)
        objs.append(ob)
        meshes.append(mesh)
        lods.append((f'{name}_LOD{lod}.fbx', pg.LOD_SCREEN[lod], [ids[m] for m in used], True))
        rep['lods'].append(dict(lod=lod, tris=mesh.tris, k=round(k, 3), sil=best[2]))
        ob.hide_render = True
    shutil.rmtree(tmp, ignore_errors=True)
    # крест из LOD0 (функции pine_gen: центр кроны — свой)
    pg.crown_centre = lambda tree: tree.crown_c
    o0.hide_render = False
    bake = pg.bake_cross(info, o0, pm, folder, name)
    for s, m in zip(o0.material_slots, [prev_mats[m] for m in used]):
        s.material = m
    cmesh = pg.cross_mesh(info, bake)
    cross_names = pg.save_cross(bake, folder, name)
    cross_id = name + '_cross'
    cross_prev = pg.card_material(cross_id, os.path.join(folder, cross_names[0]), 0.5,
                                  normal=os.path.join(folder, cross_names[1]), cull_back=True)
    cobj = cmesh.to_object(f'{name}_LOD3', [None, None, cross_prev], [M_LEAVES])
    # цвет креста под LOD2: оба без теней, солнце + небо, 3 камеры
    o0.hide_render = True
    lod2 = objs[2]
    pg.add_nrm_attr(lod2)
    sc = bpy.context.scene
    sc.render.film_transparent = True
    sc.cycles.samples = 24
    sc.render.resolution_x, sc.render.resolution_y = 320, 320
    sun = pg.light_scene(sc)
    for o in (lod2, cobj):
        o.visible_shadow = False
    Ht = info.H
    cams = [((0, -Ht * 2.6, Ht * 0.55), (0, 0, Ht * 0.75)), ((Ht * 2.6, 0, Ht * 0.6), (0, 0, Ht * 0.75)),
            ((0, -Ht * 1.6, Ht * 2.0), (0, 0, Ht * 0.7))]
    gains = []
    for it in range(4):
        cobj.hide_render, lod2.hide_render = True, False
        c2 = pg.lit_crown_colour(sc, lod2, info, cams)
        cobj.hide_render, lod2.hide_render = False, True
        cx = pg.lit_crown_colour(sc, cobj, info, cams)
        g = np.clip(c2 / np.maximum(cx, 1e-4), 0.7, 1.4)
        gains.append([round(float(x), 3) for x in g])
        if np.all(np.abs(g - 1) < 0.03) or it == 3:
            break
        bake['albedo'][..., :3] = np.clip(bake['albedo'][..., :3] * g ** 1.6, 0, 1)
        pg.save_cross(bake, folder, name)
        for im in bpy.data.images:
            if im.filepath and os.path.basename(im.filepath) == cross_names[0]:
                im.reload()
    rep['cross_gain'] = gains
    for o in (lod2, cobj):
        o.visible_shadow = True
    bpy.data.objects.remove(sun)
    pg.remove_nrm_attr(o0)
    pg.remove_nrm_attr(lod2)
    objs.append(cobj)
    lods.append((f'{name}_LOD3.fbx', pg.LOD_SCREEN[3], [cross_id], False))
    rep['lods'].append(dict(lod=3, tris=cmesh.tris))
    for o in objs:
        o.hide_render = False
    for o, (fbx, _, _, _) in zip(objs, lods):
        eg.export_fbx([o], os.path.join(folder, fbx))
    materials = [
        dict(id='bark', role='bark', albedo=tex['bark'][0], normal=tex['bark'][1], mask=tex['bark'][2], tiling=[1, 1],
             smoothnessMax=1.0, aoMax=1.0, normalScale=1.0, **WIND),
        dict(id='needles', role='leaves', albedo=tex['needles'][0], normal=tex['needles'][1], mask=tex['needles'][2],
             alphaCutoff=0.5, smoothnessMax=0.3, aoMax=1.0, normalScale=1.0, puddleSink=0.0, rotorWashClear=0.0,
             shiverDrag=0.04, healthyColor=[1.0, 1.0, 1.0], dryColor=[0.88, 0.9, 0.82], **WIND),
        dict(id=cross_id, role='cross', albedo=cross_names[0], normal=cross_names[1], alphaCutoff=0.5,
             healthyColor=[1.0, 1.0, 1.0], dryColor=[0.88, 0.9, 0.82], scaleFadeLength=400.0, shiverDrag=0.04, **WIND),
    ]
    height = round(info.H, 2)
    data = eg.write_manifest(folder, name, 'tree', GROUP,
                             [dict(fbx=f, screen=s, submeshes=i, castShadows=c) for f, s, i, c in lods],
                             materials, height=height, trunk_radius=round(info.trunk_radius, 3), collider_height=height)
    data['evergreen'] = True
    with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=1)
    report.append(rep)
    return data


def main():
    out_root = eg.script_args()[0]
    tex_dir = os.path.join(out_root, '_tex')
    work = os.path.join(out_root, '_work')
    os.makedirs(tex_dir, exist_ok=True)
    os.makedirs(work, exist_ok=True)
    tex, means = make_textures(tex_dir)
    sources = {name: prepare(obj) for obj, name in TREES.items()}
    report = [dict(textures=means)]
    for name, src in sources.items():
        # сцена набора (земля, солнце, остальные деревья) в рендеры креста и силуэтов не попадает
        for o in list(bpy.data.objects):
            bpy.data.objects.remove(o)
        build_asset(src, name, out_root, tex, report)
        print('ASSET', json.dumps(report[-1]))
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(work, name + '.blend'), check_existing=False, copy=True)
    with open(os.path.join(work, 'report.json'), 'w') as f:
        json.dump(report, f, indent=1)


if __name__ == '__main__':
    main()
