# Общий каркас деревьев (Tools/TreeGen): скелет ветвей -> меш коры, карточки листвы/хвои, LOD, дальняя карточка-крест,
# манифест для Unity (VegetationImporter). Видовые генераторы (pine_gen.py, oak_gen.py) строят скелет (Branch) и
# решают, где карточки; всё остальное — здесь.
#
# Соглашения (проверены на Blender 5.2 -> Unity 6):
#  - метры, Blender Z вверх (в Unity Y вверх), пивот — основание ствола в (0, 0, 0);
#  - подмеши: материалы по порядку списка material_ids, в манифесте те же id;
#  - цвет вершин: альфа = вес ветра (0 у комля, растёт по ветвям, листва 0.3..1 к краю кроны); у листвы RGB = A
#    (как у деревьев NatureManufacture), у коры RGB = 0;
#  - UV0: кора — U вокруг (повторы по окружности), V вдоль по длине в тех же метрах (текстура не тянется);
#    карточки — в ячейку атласа; UV1 (Unity uv2) — нормированная высота в дереве (на будущее, шейдеры NM им не пользуются);
#  - дальняя карточка: 4 вида дерева (спереди, справа, сзади, слева) в атласе 4:1, меш — два креста из 4 полуплоскостей
#    от ствола, каждая со своим видом; нормали наклонены вверх-наружу, как у креста NatureManufacture.
import math
import os
import random

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

import envgen_common as eg

UP = Vector((0.0, 0.0, 1.0))


class Branch:
    """Ветвь — ломаная с радиусами в узлах. level: 0 ствол, 1 скелетные ветви, 2 ветви, 3 веточки.
    weight0/weight1 — вес ветра в начале и конце (для коры), alive=False — сухой сук без листвы."""

    def __init__(self, points, radii, level, parent=None, alive=True):
        self.points = [Vector(p) for p in points]
        self.radii = list(radii)
        self.level = level
        self.parent = parent
        self.children = []
        self.alive = alive
        self.weight0 = 0.0
        self.weight1 = 0.0
        if parent is not None:
            parent.children.append(self)

    @property
    def length(self):
        return sum((self.points[i + 1] - self.points[i]).length for i in range(len(self.points) - 1))

    def point_at(self, t):
        """Точка, радиус и направление на доле длины t (0..1)."""
        target = t * self.length
        acc = 0.0
        for i in range(len(self.points) - 1):
            seg = (self.points[i + 1] - self.points[i]).length
            if acc + seg >= target or i == len(self.points) - 2:
                f = 0.0 if seg < 1e-9 else min(1.0, max(0.0, (target - acc) / seg))
                p = self.points[i].lerp(self.points[i + 1], f)
                r = self.radii[i] + (self.radii[i + 1] - self.radii[i]) * f
                d = (self.points[i + 1] - self.points[i]).normalized()
                return p, r, d
            acc += seg
        return self.points[-1], self.radii[-1], (self.points[-1] - self.points[-2]).normalized()


def grow_curve(start, direction, length, segments, rng, gravity=0.0, wobble=0.0, up_curve=0.0):
    """Точки изогнутой ветви: шаг за шагом направление наклоняется под весом (gravity > 0 вниз) и к свету
    (up_curve > 0 вверх), плюс случайные изломы wobble (рад на шаг)."""
    pts = [Vector(start)]
    d = Vector(direction).normalized()
    step = length / segments
    for i in range(segments):
        d = d + Vector((0, 0, -gravity * step)) + Vector((0, 0, up_curve * step))
        if wobble > 0:
            axis = d.orthogonal().normalized()
            axis.rotate(Quaternion(d, rng.uniform(0, math.tau)))
            d.rotate(Quaternion(axis, rng.gauss(0, wobble)))
        d.normalize()
        pts.append(pts[-1] + d * step)
    return pts


def sweep(mb, branch, material, sides, bark_repeat_m, rng, cap=True, min_sides=3):
    """Кора ветви: кольца по узлам, швы UV на u=1. Цвет: RGB 0, A — вес ветра вдоль ветви."""
    pts = branch.points
    n = len(pts)
    if n < 2:
        return
    sides = max(min_sides, sides)
    frames = []
    prev_normal = None
    for i in range(n):
        if i == 0:
            t = (pts[1] - pts[0]).normalized()
        elif i == n - 1:
            t = (pts[-1] - pts[-2]).normalized()
        else:
            t = ((pts[i + 1] - pts[i]).normalized() + (pts[i] - pts[i - 1]).normalized()).normalized()
        if prev_normal is None:
            normal = t.orthogonal().normalized()
        else:
            normal = (prev_normal - t * prev_normal.dot(t)).normalized()  # перенос рамки без скручивания
        prev_normal = normal
        frames.append((t, normal, t.cross(normal).normalized()))
    circumference = max(2 * math.pi * max(branch.radii[0], 1e-3), 1e-3)
    u_repeats = max(1, round(circumference / bark_repeat_m))
    v = 0.0
    rings = []
    for i in range(n):
        if i > 0:
            v += (pts[i] - pts[i - 1]).length / bark_repeat_m
        t, nx, ny = frames[i]
        r = max(branch.radii[i], 0.002)
        w = branch.weight0 + (branch.weight1 - branch.weight0) * (i / (n - 1))
        ring = []
        for s in range(sides + 1):
            a = s / sides * math.tau
            off = nx * math.cos(a) + ny * math.sin(a)
            ring.append((mb.vert(pts[i] + off * r), (s / sides * u_repeats, v), w))
        rings.append(ring)
    hmax = max(p.z for p in pts) or 1.0
    for i in range(n - 1):
        for s in range(sides):
            a, b, c, d = rings[i][s], rings[i][s + 1], rings[i + 1][s + 1], rings[i + 1][s]
            mb.face([a[0], b[0], c[0], d[0]], material, [a[1], b[1], c[1], d[1]],
                    [(0, pts[i].z / hmax), (0, pts[i].z / hmax), (0, pts[i + 1].z / hmax), (0, pts[i + 1].z / hmax)],
                    [(0, 0, 0, a[2]), (0, 0, 0, b[2]), (0, 0, 0, c[2]), (0, 0, 0, d[2])])
    if cap and branch.radii[-1] > 0.004:
        last = rings[-1]
        tip = mb.vert(pts[-1] + frames[-1][0] * branch.radii[-1] * 0.5)
        for s in range(sides):
            a, b = last[s], last[s + 1]
            mb.face([a[0], b[0], tip], material, [a[1], b[1], (a[1][0], a[1][1] + 0.05)], None,
                    [(0, 0, 0, a[2]), (0, 0, 0, b[2]), (0, 0, 0, a[2])])


def bark_normals(obj):
    eg.smooth_normals(obj)


class Card:
    """Карточка листвы: центр, ось «вдоль» (от веточки наружу), нормаль, размер (ширина, длина), ячейка атласа, вес ветра."""

    def __init__(self, center, along, normal, width, length, cell, weight, anchor=0.15):
        self.center = Vector(center)
        self.along = Vector(along).normalized()
        self.normal = Vector(normal).normalized()
        self.width = width
        self.length = length
        self.cell = cell
        self.weight = weight
        self.anchor = anchor  # доля длины карточки позади точки крепления


def emit_cards(mb, cards, material, atlas_grid, crown_center, crown_radius, normal_bend=0.55, normals=None,
               double=False):
    """Квады карточек в меш; normals — список нормалей на углах (заполняется): нормаль карточки, наклонённая к нормали
    «сферы кроны» (мягкий объём, как у листвы NatureManufacture). atlas_grid = (столбцов, строк)."""
    cols, rows = atlas_grid
    for card in cards:
        side = card.along.cross(card.normal).normalized()
        n_card = card.along.cross(side).normalized()
        if n_card.dot(card.normal) < 0:
            n_card = -n_card
        base = card.center - card.along * card.length * card.anchor
        p0 = base - side * card.width * 0.5
        p1 = base + side * card.width * 0.5
        p2 = p1 + card.along * card.length
        p3 = p0 + card.along * card.length
        ci = card.cell % cols
        cj = card.cell // cols
        u0, u1 = ci / cols, (ci + 1) / cols
        v0, v1 = 1 - (cj + 1) / rows, 1 - cj / rows
        uvs = [(u0, v0), (u1, v0), (u1, v1), (u0, v1)]
        w0 = card.weight * 0.85
        w1 = min(1.0, card.weight * 1.1 + 0.05)
        cols_ = [(w0, w0, w0, w0), (w0, w0, w0, w0), (w1, w1, w1, w1), (w1, w1, w1, w1)]
        idx = [mb.vert(p) for p in (p0, p1, p2, p3)]
        corner_n = []
        for p in (p0, p1, p2, p3):
            sphere = (p - crown_center)
            sphere = sphere.normalized() if sphere.length > 1e-6 else UP
            corner_n.append((n_card * (1 - normal_bend) + sphere * normal_bend + UP * 0.15).normalized())
        hmax = max(crown_center.z + crown_radius, 1.0)
        mb.face(idx, material, uvs, [(0, p.z / hmax) for p in (p0, p1, p2, p3)], cols_)
        if normals is not None:
            normals.extend(corner_n)


def set_mesh_normals(obj, bark_material, card_normals):
    """Кора — гладкие нормали, карточки — свои (в порядке граней)."""
    mesh = obj.data
    mesh.update()
    loop_normals = []
    k = 0
    corner = [c.vector.copy() for c in mesh.corner_normals]
    for poly in mesh.polygons:
        if poly.material_index in bark_material:
            # гладкая нормаль коры: нормаль вершины
            for li in poly.loop_indices:
                loop_normals.append(mesh.vertices[mesh.loops[li].vertex_index].normal.copy())
        else:
            for li in poly.loop_indices:
                loop_normals.append(card_normals[k] if k < len(card_normals) else corner[li])
                k += 1
    mesh.normals_split_custom_set(loop_normals)


# ------------------------------------------------------------------ дальняя карточка

def render_cross_atlas(scene, objects, height, width, out_albedo, out_normal, slot=512, samples=24):
    """4 вида дерева (с +Y, +X, -Y, -X Blender) в атлас 4:1. Возвращает (ortho_scale, z_center) для меша креста."""
    size = max(height, width) * 1.04
    eg.setup_card_render(scene, slot, slot, samples=samples)
    zc = size * 0.5
    views = []
    tmp = os.path.join(os.path.dirname(out_albedo), '_cross_tmp')
    os.makedirs(tmp, exist_ok=True)
    albedo_mat = None
    normal_mat = eg.view_normal_material('cross_normal')
    for k in range(4):
        a = k * math.pi / 2
        # камера смотрит на ствол с направления a (0: с -Y на +Y, то есть вид «спереди»)
        loc = Vector((math.sin(a) * 200, -math.cos(a) * 200, zc))
        cam = eg.ortho_camera(scene, loc, (math.pi / 2, 0, a), size, name=f'cross_cam_{k}')
        scene.camera = cam
        pa = os.path.join(tmp, f'a{k}.png')
        pn = os.path.join(tmp, f'n{k}.png')
        scene.render.filepath = pa
        bpy.ops.render.render(write_still=True)
        eg.render_with_override(scene, objects, normal_mat, pn)
        views.append((pa, pn))
        bpy.data.objects.remove(cam)
    al = np.zeros((slot, slot * 4, 4), np.float32)
    nm = np.zeros((slot, slot * 4, 4), np.float32)
    for k, (pa, pn) in enumerate(views):
        a = eg.srgb_png_to_linear_values(pa)
        n = eg.srgb_png_to_linear_values(pn)
        al[:, k * slot:(k + 1) * slot] = a
        nm[:, k * slot:(k + 1) * slot, :3] = n[..., :3]
        nm[:, k * slot:(k + 1) * slot, 3] = a[..., 3]
    al = eg.dilate_rgb(al, 6)
    eg.save_png(al, out_albedo)
    nm[..., :3] = np.where(nm[..., 3:4] > 0.01, nm[..., :3], np.array([0.5, 0.5, 1.0], np.float32))
    eg.save_png(nm[..., :3], out_normal, linear=True)
    return size, zc


def cross_mesh(size, zc, material=0):
    """Четыре полуплоскости от оси ствола (Blender): к +X — вид 0 (камера с -Y), к +Y — вид 1, к -X — вид 2, к -Y — вид 3.
    NM_Cross рисует только лицевую сторону (Cull Back), поэтому у каждой полуплоскости две грани: лицевая (к камере вида k)
    — правая половина вида k, обратная (к камере противоположного вида k+2) — левая половина вида k+2: с той стороны
    полуплоскость лежит слева в кадре. Нормали наклонены наружу и вверх, как у креста NatureManufacture."""
    mb = eg.MeshBuilder()
    normals = []
    half = size * 0.5
    z0, z1 = zc - half, zc + half
    rows = 4
    for k in range(4):
        a = k * math.pi / 2
        right = Vector((math.cos(a), math.sin(a), 0))
        facing = Vector((math.sin(a), -math.cos(a), 0))  # к камере вида k
        k2 = (k + 2) % 4
        for side in (1, -1):
            u_center = ((k if side > 0 else k2) + 0.5) / 4
            u_edge = (k + 1.0) / 4 if side > 0 else k2 / 4
            for j in range(rows):
                za, zb = z0 + (z1 - z0) * j / rows, z0 + (z1 - z0) * (j + 1) / rows
                va, vb = j / rows, (j + 1) / rows
                p = [Vector((0, 0, za)), right * half + Vector((0, 0, za)), right * half + Vector((0, 0, zb)), Vector((0, 0, zb))]
                uv = [(u_center, va), (u_edge, va), (u_edge, vb), (u_center, vb)]
                cols = [(0.25 * ((q.z - z0) / max(z1 - z0, 1e-3)) ** 1.5,) * 4 for q in p]
                if side < 0:  # обратная грань: обход наоборот
                    p = [p[0], p[3], p[2], p[1]]
                    uv = [uv[0], uv[3], uv[2], uv[1]]
                    cols = [cols[0], cols[3], cols[2], cols[1]]
                idx = [mb.vert(q) for q in p]
                mb.face(idx, material, uv, uv, cols)
                for q in p:
                    h = (q.z - z0) / max(z1 - z0, 1e-3)
                    outward = (q.length > 0.01) * 0.25
                    n = (facing * 0.6 * side + right * outward + UP * (0.35 + 0.4 * h)).normalized()
                    normals.append(n)
    return mb, normals


def write_tree(out_dir, name, group, lods, materials, height, trunk_radius, clear_height):
    """lods: [(fbx_name, screen, [material ids], cast_shadows)], materials: [dict(id, role, albedo, normal, mask, ...)]."""
    return eg.write_manifest(out_dir, name, 'tree', group,
                             [dict(fbx=f, screen=s, submeshes=ids, castShadows=c) for f, s, ids, c in lods],
                             materials, height=height, trunk_radius=trunk_radius, collider_height=clear_height)


def preview_tree(objects, out_png, cam_distance, cam_height, target_z, lens=35, size=(720, 720), sky=(0.55, 0.65, 0.8)):
    """Предпросмотр дерева в Cycles: солнце, небо, земля."""
    scene = bpy.context.scene
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = 3.5
    so = bpy.data.objects.new('sun', sun)
    eg.link(so)
    so.rotation_euler = (math.radians(50), 0, math.radians(40))
    world = bpy.data.worlds.new('w')
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Color'].default_value = (*sky, 1)
    cam = bpy.data.objects.new('pcam', bpy.data.cameras.new('pcam'))
    eg.link(cam)
    cam.data.lens = lens
    cam.location = (0, -cam_distance, cam_height)
    direction = Vector((0, 0, target_z)) - cam.location
    cam.rotation_euler = direction.to_track_quat('-Z', 'Y').to_euler()
    scene.camera = cam
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 32
    scene.cycles.device = 'CPU'
    scene.cycles.use_denoising = True
    scene.render.film_transparent = False
    scene.render.resolution_x, scene.render.resolution_y = size
    scene.view_settings.view_transform = 'AgX'
    scene.render.filepath = out_png
    bpy.ops.render.render(write_still=True)
    return out_png
