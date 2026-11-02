# Общее для генераторов растительности в Blender (Tools/TreeGen): деревья, трава, ветки, камни для Unity.
# Запуск генератора — фоном, без открытого Blender:
#   "C:/Program Files/Blender Foundation/Blender 5.2/blender.exe" -b --factory-startup --python <script.py> -- <args>
# Проверено на Blender 5.2.1 и Unity 6000.3:
#  - FBX (export_fbx): Blender Z вверх -> Unity Y вверх, масштаб 1, корень без поворота; X при смене руки зеркалится;
#  - цвет вершин (атрибут BYTE_COLOR на углах, colors_type='LINEAR') приходит в Unity как есть — альфа = вес ветра
#    (шейдеры NatureManufacture умножают на неё силу ветра: 0 у корня/комля, 1 на кончиках);
#  - второй UV-слой приходит как UV1 (Unity uv2);
#  - рендер карточек (render_card): ортокамера, прозрачный фон, вывод через эмиссию: альбедо — как sRGB-картинка, нормали —
#    в пространстве камеры (X вправо, Y вверх, Z к камере), то есть сразу касательные нормали карточки в соглашении Unity
#    (OpenGL), записанные линейно.
import math
import os
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

BLENDER = r"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"


def script_args():
    """Аргументы после '--' в командной строке Blender."""
    return sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    return bpy.context.scene


def link(obj, scene=None):
    (scene or bpy.context.scene).collection.objects.link(obj)
    return obj


class MeshBuilder:
    """Сборщик меша: вершины, треугольники/четырёхугольники по подмешам (материалам), UV0/UV1 и цвет на углах."""

    def __init__(self):
        self.verts = []
        self.faces = []  # (indices, material, uv0 per corner, uv1 per corner, colour per corner)

    def vert(self, co):
        self.verts.append(tuple(co))
        return len(self.verts) - 1

    def face(self, indices, material=0, uv0=None, uv1=None, colors=None):
        n = len(indices)
        self.faces.append((tuple(indices), material, uv0 or [(0.0, 0.0)] * n, uv1 or [(0.0, 0.0)] * n,
                           colors or [(1.0, 1.0, 1.0, 1.0)] * n))

    def append(self, other, matrix=None):
        offset = len(self.verts)
        for v in other.verts:
            self.verts.append(tuple(matrix @ Vector(v)) if matrix is not None else v)
        for idx, mat, uv0, uv1, col in other.faces:
            self.faces.append((tuple(i + offset for i in idx), mat, uv0, uv1, col))

    @property
    def triangle_count(self):
        return sum(len(f[0]) - 2 for f in self.faces)

    def to_object(self, name, materials):
        """materials: список материалов Blender (индекс = подмеш)."""
        mesh = bpy.data.meshes.new(name)
        bm = bmesh.new()
        bverts = [bm.verts.new(v) for v in self.verts]
        bm.verts.ensure_lookup_table()
        uv0_layer = bm.loops.layers.uv.new('UVMap')
        uv1_layer = bm.loops.layers.uv.new('UV2')
        col_layer = bm.loops.layers.color.new('Col')
        for idx, mat, uv0, uv1, col in self.faces:
            try:
                f = bm.faces.new([bverts[i] for i in idx])
            except ValueError:
                continue  # вырожденная или повторная грань
            f.material_index = mat
            for loop, a, b, c in zip(f.loops, uv0, uv1, col):
                loop[uv0_layer].uv = a
                loop[uv1_layer].uv = b
                loop[col_layer] = c
        bm.to_mesh(mesh)
        bm.free()
        # цвет углов bmesh — FLOAT_COLOR; для FBX нужен BYTE_COLOR на углах
        src = mesh.color_attributes.get('Col')
        if src is not None and (src.data_type != 'BYTE_COLOR' or src.domain != 'CORNER'):
            values = [tuple(d.color) for d in src.data]
            mesh.color_attributes.remove(src)
            dst = mesh.color_attributes.new(name='Col', type='BYTE_COLOR', domain='CORNER')
            for d, v in zip(dst.data, values):
                d.color = v
        for m in materials:
            mesh.materials.append(m)
        obj = bpy.data.objects.new(name, mesh)
        link(obj)
        return obj


def smooth_normals(obj, angle_deg=180.0):
    """Гладкие нормали (кора); карточки листвы обычно оставляют плоскими или наклоняют сами."""
    mesh = obj.data
    for p in mesh.polygons:
        p.use_smooth = True
    if hasattr(mesh, 'set_sharp_from_angle'):
        mesh.set_sharp_from_angle(angle=math.radians(angle_deg))


def set_custom_normals(obj, normals_per_loop):
    """Свои нормали на углах (карточки листвы, наклонённые к «сфере» кроны; трава вверх)."""
    mesh = obj.data
    mesh.normals_split_custom_set([tuple(n) for n in normals_per_loop])


def export_fbx(objects, path):
    """FBX для Unity: метры, Y вверх, без поворота корня, цвет вершин линейно, нормали из меша."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT')
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, apply_unit_scale=True,
                             apply_scale_options='FBX_SCALE_UNITS', axis_forward='-Z', axis_up='Y',
                             bake_space_transform=True, mesh_smooth_type='OFF', colors_type='LINEAR',
                             use_tspace=False, use_mesh_modifiers=True, add_leaf_bones=False, bake_anim=False,
                             path_mode='STRIP')
    return path


# ---------------------------------------------------------------- материалы для рендера карточек

def emission_material(name, color_socket_builder):
    """Материал с эмиссией; color_socket_builder(node_tree) -> выход цвета. Освещения нет — пишется чистый цвет."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    em = nt.nodes.new('ShaderNodeEmission')
    nt.links.new(color_socket_builder(nt), em.inputs['Color'])
    nt.links.new(em.outputs['Emission'], out.inputs['Surface'])
    return m


def view_normal_material(name='card_normal'):
    """Нормаль в пространстве камеры -> цвет 0..1 (X вправо, Y вверх, Z к камере) — касательная нормаль карточки."""
    def build(nt):
        g = nt.nodes.new('ShaderNodeNewGeometry')
        vt = nt.nodes.new('ShaderNodeVectorTransform')
        vt.vector_type = 'NORMAL'
        vt.convert_from = 'WORLD'
        vt.convert_to = 'CAMERA'
        mad = nt.nodes.new('ShaderNodeVectorMath')
        mad.operation = 'MULTIPLY_ADD'
        # в пространстве камеры Blender Z смотрит от камеры: нормаль к камере — Z < 0
        mad.inputs[1].default_value = (0.5, 0.5, -0.5)
        mad.inputs[2].default_value = (0.5, 0.5, 0.5)
        nt.links.new(g.outputs['Normal'], vt.inputs['Vector'])
        nt.links.new(vt.outputs['Vector'], mad.inputs[0])
        return mad.outputs['Vector']
    return emission_material(name, build)


def ortho_camera(scene, location, rotation_euler, ortho_scale, name='card_cam'):
    data = bpy.data.cameras.new(name)
    data.type = 'ORTHO'
    data.ortho_scale = ortho_scale
    data.clip_start = 0.01
    data.clip_end = 1000.0
    cam = bpy.data.objects.new(name, data)
    link(cam, scene)
    cam.location = location
    cam.rotation_euler = rotation_euler
    scene.camera = cam
    return cam


def setup_card_render(scene, width, height, samples=32):
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = samples
    scene.cycles.device = 'CPU'
    scene.cycles.use_denoising = False
    scene.render.filter_size = 1.0
    scene.render.film_transparent = True
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.image_settings.color_depth = '8'


def render_with_override(scene, objects, material, path):
    """Рендер, где у всех objects один материал (эмиссия): альбедо, нормали, высота..."""
    saved = []
    for o in objects:
        if o.type != 'MESH':
            continue
        saved.append((o, [s.material for s in o.material_slots]))
        for s in o.material_slots:
            s.material = material
        if not o.material_slots:
            o.data.materials.append(material)
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    for o, mats in saved:
        for s, m in zip(o.material_slots, mats):
            s.material = m
    return path


def srgb_png_to_linear_values(path):
    """PNG, записанный через sRGB (Standard), -> линейные значения 0..1 (H, W, 4), строки сверху вниз."""
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)[::-1].copy()
    bpy.data.images.remove(img)
    # pixels байтовой картинки — как записаны (sRGB); вывод Standard кодировал линейные значения в sRGB — раскодируем
    rgb = px[..., :3]
    px[..., :3] = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    return px


def save_png(array, path, linear=False):
    """array: (H, W, 3|4) 0..1, строки сверху вниз. Цвет (linear=False) — линейные значения, пишутся в sRGB;
    данные (linear=True: нормали, маски) — как есть."""
    h, w = array.shape[:2]
    array = np.asarray(array, np.float32)
    if array.shape[2] == 3:
        array = np.concatenate([array, np.ones((h, w, 1), np.float32)], axis=2)
    if not linear:
        array = array.copy()
        rgb = np.clip(array[..., :3], 0, 1)
        array[..., :3] = np.where(rgb <= 0.0031308, rgb * 12.92, 1.055 * rgb ** (1 / 2.4) - 0.055)
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=True, float_buffer=False)
    img.colorspace_settings.name = 'Non-Color' if linear else 'sRGB'
    img.pixels = np.clip(array[::-1], 0, 1).astype(np.float32).ravel()
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    bpy.data.images.remove(img)
    return path


def dilate_rgb(rgba, iterations=8):
    """Растекание цвета за край альфы (иначе в мипах карточки по краю тёмная кайма)."""
    rgb = rgba[..., :3].copy()
    a = rgba[..., 3] > 0.01
    filled = a.copy()
    for _ in range(iterations):
        acc = np.zeros_like(rgb)
        cnt = np.zeros(a.shape, np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            sh = np.roll(np.roll(filled, dy, 0), dx, 1)
            acc += np.roll(np.roll(rgb, dy, 0), dx, 1) * sh[..., None]
            cnt += sh
        grow = (~filled) & (cnt > 0)
        rgb[grow] = acc[grow] / cnt[grow][:, None]
        filled |= grow
    out = rgba.copy()
    out[..., :3] = rgb
    return out


def grade_albedo(rgb, target, saturation, weights=None):
    """Цветокоррекция альбедо под палитру сцены: насыщенность × saturation вокруг яркости пикселя, затем усиление по
    каналам — к среднему target (линейный цвет; среднее — по weights, если даны). Рисунок текстуры остаётся. Фотосканы и
    фото-текстуры сняты в своём свете (тёплые, насыщенные, ярче), и рядом с корой деревьев и землёй сцены выбивались."""
    rgb = np.asarray(rgb, np.float32)
    lum = rgb @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    out = lum[..., None] + (rgb - lum[..., None]) * saturation
    flat = out.reshape(-1, 3)
    if weights is None:
        mean = flat.mean(0)
    else:
        w = np.asarray(weights, np.float32).reshape(-1)
        mean = (flat * w[:, None]).sum(0) / max(float(w.sum()), 1e-6)
    gain = np.asarray(target, np.float32) / np.maximum(mean, 1e-4)
    return np.clip(out * gain, 0.0, 1.0)


def write_prop_twin(folder, manifest, screen=0.004):
    """Рядом с деталью Nature Renderer (manifest, его папка folder) — её же объект с коллайдером (<name>_prop: тип prop —
    VegetationImporter собирает LODGroup с MeshCollider по LOD0; меш, текстуры и материалы те же). Для точки взлёта: её
    объекты — GameObject, и дрон о них бьётся."""
    import json
    import shutil
    name = manifest['name'] + '_prop'
    twin = os.path.join(os.path.dirname(folder), name)
    os.makedirs(twin, exist_ok=True)
    for f in os.listdir(folder):
        if f != 'manifest.json' and os.path.isfile(os.path.join(folder, f)):
            shutil.copyfile(os.path.join(folder, f), os.path.join(twin, f))
    data = dict(manifest, name=name, type='prop', nr=None)
    data['lods'] = [dict(lod, screen=screen) for lod in manifest['lods']]
    with open(os.path.join(twin, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=1)
    return twin


def lerp(a, b, t):
    return a + (b - a) * t


def hex_rgb(h):
    """'#rrggbb' -> линейный цвет (для материалов Blender)."""
    h = h.lstrip('#')
    srgb = [int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4)]
    return tuple(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb)


def write_manifest(folder, name, type_, group, lods, materials, height=0.0, trunk_radius=0.0, collider_height=0.0):
    """manifest.json для импорта в Unity (VegetationImporter): lods — [{'fbx', 'screen', 'submeshes', 'castShadows'}],
    materials — [{'id', 'role' (bark|leaves|cross|grass), 'albedo', 'normal', 'mask', ...переопределения свойств}]."""
    import json
    data = dict(name=name, type=type_, group=group, height=height, trunkRadius=trunk_radius,
                colliderHeight=collider_height, lods=lods, materials=materials)
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=1)
    return data
