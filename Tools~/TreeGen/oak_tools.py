# Вспомогательное для проверки текстур и превью дуба (фоновый Blender):
#   oak_tools.py -- barktest <tex_dir> <out.png>        кора на цилиндре под низким солнцем (ствол и ветка)
#   oak_tools.py -- tile <png> <out.png> [nx ny]          плитка nx x ny (проверка швов)
#   oak_tools.py -- sheet <out.png> <png> [png...]        контактный лист (до 4 в ряд, по 640 px)
#   oak_tools.py -- crop <png> <out.png> x0 y0 x1 y1      вырез (доли 0..1, y сверху)
#   oak_tools.py -- stats <png> [mask_alpha]              средний цвет (линейное среднее -> sRGB), покрытие альфы
#   oak_tools.py -- winter <albedo.png> <out.png>         атлас глазами шейдера сезона: зелёные тексели прочь
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402
import numpy as np  # noqa: E402
from mathutils import Vector  # noqa: E402

import envgen_common as eg  # noqa: E402


def load(path):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1].copy()
    bpy.data.images.remove(img)
    return px  # как записано (sRGB-коды 0..1)


def save_raw(arr, path):
    eg.save_png(arr, path, linear=True)


def resize(arr, w, h):
    H, W = arr.shape[:2]
    ys = (np.arange(h) + 0.5) * H / h
    xs = (np.arange(w) + 0.5) * W / w
    return arr[np.clip(ys.astype(int), 0, H - 1)][:, np.clip(xs.astype(int), 0, W - 1)]


def to_lin(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def cmd_tile(args):
    a = load(args[0])
    nx, ny = (int(args[2]), int(args[3])) if len(args) > 3 else (2, 2)
    t = np.tile(a, (ny, nx, 1))
    save_raw(t, args[1])


def cmd_sheet(args):
    out, files = args[0], args[1:]
    cw = 640
    tiles = []
    for f in files:
        a = load(f)
        h, w = a.shape[:2]
        ch = int(round(cw * h / w))
        r = resize(a, cw, ch)
        bg = np.ones_like(r) * np.array([0.75, 0.75, 0.75, 1.0], np.float32)
        r = r * r[..., 3:4] + bg * (1 - r[..., 3:4])
        r[..., 3] = 1
        tiles.append(r)
    per = min(4, len(tiles))
    rows = [tiles[i:i + per] for i in range(0, len(tiles), per)]
    H = sum(max(t.shape[0] for t in row) for row in rows)
    sheet = np.ones((H, cw * per, 4), np.float32) * 0.2
    y = 0
    for row in rows:
        rh = max(t.shape[0] for t in row)
        for i, t in enumerate(row):
            sheet[y:y + t.shape[0], i * cw:i * cw + cw] = t
        y += rh
    save_raw(sheet, out)


def cmd_crop(args):
    a = load(args[0])
    h, w = a.shape[:2]
    x0, y0, x1, y1 = [float(v) for v in args[2:6]]
    save_raw(a[int(y0 * h):int(y1 * h), int(x0 * w):int(x1 * w)], args[1])


def cmd_stats(args):
    a = load(args[0])
    m = a[..., 3] > 0.5 if len(args) > 1 else np.ones(a.shape[:2], bool)
    lin = to_lin(a[..., :3])[m]
    mean = lin.mean(0)
    s = to_srgb(mean)
    print('STATS', args[0], '#' + ''.join(f'{int(round(v * 255)):02x}' for v in s), 'coverage', round(float((a[..., 3] > 0.5).mean()), 4),
          'lin', np.round(mean, 4).tolist())


def cmd_winter(args):
    a = load(args[0])
    rgb = a[..., :3]
    green = (rgb[..., 1] > rgb[..., 0] * 1.02) & (rgb[..., 1] > rgb[..., 2])
    keep = (a[..., 3] > 0.5) & ~green
    out = np.ones_like(a) * np.array([0.85, 0.85, 0.85, 1], np.float32)
    out[keep] = a[keep]
    out[..., 3] = 1
    print('WINTER kept', round(float(keep.sum() / max((a[..., 3] > 0.5).sum(), 1)), 4))
    save_raw(out, args[1])


def bark_mat(name, tex_dir, prefix, tile_scale=(1, 1)):
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    N, L = nt.nodes, nt.links
    bsdf = N['Principled BSDF']
    uv = N.new('ShaderNodeUVMap')
    mp = N.new('ShaderNodeMapping')
    mp.inputs['Scale'].default_value = (tile_scale[0], tile_scale[1], 1)
    L.new(uv.outputs[0], mp.inputs[0])
    ims = {}
    for s in ('albedo', 'normal', 'mask'):
        t = N.new('ShaderNodeTexImage')
        t.image = bpy.data.images.load(os.path.join(tex_dir, f'{prefix}_{s}.png'), check_existing=True)
        if s != 'albedo':
            t.image.colorspace_settings.name = 'Non-Color'
        L.new(mp.outputs[0], t.inputs[0])
        ims[s] = t
    L.new(ims['albedo'].outputs['Color'], bsdf.inputs['Base Color'])
    nm = N.new('ShaderNodeNormalMap')
    L.new(ims['normal'].outputs['Color'], nm.inputs['Color'])
    L.new(nm.outputs[0], bsdf.inputs['Normal'])
    ims['mask'].image.alpha_mode = 'CHANNEL_PACKED'
    inv = N.new('ShaderNodeMath')
    inv.operation = 'SUBTRACT'
    inv.inputs[0].default_value = 1.0
    L.new(ims['mask'].outputs['Alpha'], inv.inputs[1])
    L.new(inv.outputs[0], bsdf.inputs['Roughness'])
    return m


def cylinder(name, r, h, sides, u_rep, tile_h, mat, x=0.0):
    mb = eg.MeshBuilder()
    rings = 12
    idx = []
    for j in range(rings + 1):
        z = h * j / rings
        row = []
        for s in range(sides + 1):
            a = s / sides * math.tau
            row.append((mb.vert((x + r * math.cos(a), r * math.sin(a), z)), (s / sides * u_rep, z / tile_h)))
        idx.append(row)
    for j in range(rings):
        for s in range(sides):
            a, b, c, d = idx[j][s], idx[j][s + 1], idx[j + 1][s + 1], idx[j + 1][s]
            mb.face([a[0], b[0], c[0], d[0]], 0, [a[1], b[1], c[1], d[1]])
    o = mb.to_object(name, [mat])
    o.data.polygons.foreach_set('use_smooth', [True] * len(o.data.polygons))
    return o


def cmd_barktest(args):
    tex, out = args[0], args[1]
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import oak_textures as ot
    import oak_preview as op
    scene = eg.reset_scene()
    op.setup_world(scene, sun_elev=25, sun_az=200)
    mb = bark_mat('bark', tex, 'oak_bark')
    mbr = bark_mat('branch', tex, 'oak_branch')
    TW, TH = ot.BARK_TILE
    r = 0.26
    cylinder('trunk', r, 2.6, 48, round(2 * math.pi * r / TW), TH, mb, 0.0)
    rb = 0.06
    cylinder('branch', rb, 2.0, 24, max(1, round(2 * math.pi * rb / ot.BRANCH_TILE[0])), ot.BRANCH_TILE[1], mbr, 0.75)
    op.camera(scene, (0.35, -1.35, 1.3), (0.35, 0, 1.15), fov=55, res=(1100, 1100))
    op.render(scene, out, 64)


def cmd_fbxcheck(args):
    """Повторный импорт FBX ассета: треугольники, высота, слои UV, цвет вершин (листва RGB = A, кора RGB = 0),
    материалы по подмешам; средний вес ветра листвы."""
    import json
    folder = args[0]
    with open(os.path.join(folder, 'manifest.json'), encoding='utf-8') as f:
        man = json.load(f)
    out = []
    for lod in man['lods']:
        eg.reset_scene()
        bpy.ops.import_scene.fbx(filepath=os.path.join(folder, lod['fbx']), colors_type='LINEAR')
        o = [x for x in bpy.data.objects if x.type == 'MESH'][0]
        me = o.data
        o.data.transform(o.matrix_world)
        zs = [v.co.z for v in me.vertices]
        col = me.color_attributes[0] if me.color_attributes else None
        stats = dict(fbx=lod['fbx'], tris=sum(len(p.vertices) - 2 for p in me.polygons), zmin=round(min(zs), 3),
                     zmax=round(max(zs), 3), uv_layers=len(me.uv_layers), mats=len(me.materials),
                     color_layers=len(me.color_attributes), submeshes=len(lod['submeshes']))
        if col is not None:
            vals = np.array([tuple(d.color) for d in col.data], np.float32)
            mat_of_loop = np.zeros(len(me.loops), int)
            for p in me.polygons:
                for li in p.loop_indices:
                    mat_of_loop[li] = p.material_index
            per = {}
            for mi in sorted(set(mat_of_loop.tolist())):
                v = vals[mat_of_loop == mi]
                per[lod['submeshes'][mi] if mi < len(lod['submeshes']) else str(mi)] = dict(
                    a_mean=round(float(v[:, 3].mean()), 3), a_max=round(float(v[:, 3].max()), 3),
                    rgb_minus_a=round(float(np.abs(v[:, :3] - v[:, 3:4]).max()), 4), rgb_max=round(float(v[:, :3].max()), 3))
            stats['colour'] = per
        out.append(stats)
    print('FBXCHECK', json.dumps(out))


if __name__ == '__main__':
    a = eg.script_args()
    globals()['cmd_' + a[0]](a[1:])
