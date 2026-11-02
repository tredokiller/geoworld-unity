# Мёртвая древесина для леса (Tools/TreeGen; дизайн леса, этап 3): поваленные стволы, пни и сухие ветви — фотосканы
# Poly Haven (CC0 1.0, https://polyhaven.com: dead_tree_trunk, dead_tree_trunk_02, tree_stump_01, tree_stump_02,
# dry_branches_medium_01 — glTF 2K, скачать в одну папку, по подпапке на модель). Сканы — 5–100 тыс. треугольников;
# здесь каждый упрощается (Decimate, схлопывание рёбер) до игровой сетки, нормаль запекается с исходника в Cycles
# (выбранное в активное: геометрия скана и его карта нормалей — в касательные нормали упрощённой сетки, OpenGL, как в
# Unity), а цвет и маска берутся из текстур скана — схлопывание сохраняет развёртку. Цвет сканов — в палитру сцены
# (envgen_common.grade_albedo: насыщенность × GRADE_SATURATION, средний цвет — grade у ассета): сняты они тёплыми и
# насыщенными (красный к синему 2.7–3 против 1.1–1.4 у коры деревьев сцены — бука, тополя, дуба, яркость 0.05–0.11) и
# рядом с лесом выбивались. Текстуры 1024² (бревно в
# 3–4 м — ~3 мм на тексель): альбедо sRGB, нормаль и маска линейно; маска NatureManufacture: R металличность 0, G AO
# (из ARM скана), B высота 0.5, A гладкость (1 − шероховатость ARM). Материал — кора NatureManufacture (роль bark,
# NM_Bark NR), ветер выключен (цвет вершин (1, 1, 1, 0): NM умножает силу ветра на альфу; _Stiffness 1, _Drag 0.0001).
# Опора: бревно и ветвь лежат на земле (нижние 2 % точек — на 2 см в земле), пень — край его бугра земли на 5 см под
# землёй. Все — детали Nature Renderer (VegetationImporter, type detail): их сажает GroundDressing под деревьями леса.
#  - deadwood_log_01 — поваленный ствол 3 м, Ø 0.28 м, гнилой, со мхом;
#  - deadwood_log_02 — старый ствол 4 м с толстым суком, кора местами отвалилась (светлая древесина);
#  - deadwood_stump_01, _02 — старые пни в бугре земли с подстилкой (1.4–1.6 м с бугром);
#  - deadwood_branch_01..03 — сухие сучья 1.3, 1.0 и 0.8 м.
# Стволам и пням — ещё и объект с коллайдером (<name>_prop, envgen_common.write_prop_twin) для точки взлёта.
# Запуск (Blender 5.2, фоном):
#   blender.exe -b --factory-startup --python deadwood_gen.py -- --src <папка сканов Poly Haven> --out <папка>
import argparse
import json
import os
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

GROUP = 'Deadwood'
TEXTURE = 1024
GRADE_SATURATION = 0.55

# name, скан, объекты скана (None — все), треугольников в игре, опора, дальность Nature Renderer, средний цвет (линейный):
# мёртвая древесина чуть темнее и серее живой коры сцены (~0.095, 0.088, 0.078), пень в бугре — как лесная подстилка
WOOD = (0.085, 0.076, 0.064)
ASSETS = [
    dict(name='deadwood_log_01', prop=True, src='dead_tree_trunk', objects=None, tris=3000, pivot='ground', distance=220.0, grade=(0.07, 0.066, 0.052)),
    dict(name='deadwood_log_02', prop=True, src='dead_tree_trunk_02', objects=None, tris=4000, pivot='ground', distance=220.0, grade=(0.09, 0.08, 0.066)),
    dict(name='deadwood_stump_01', prop=True, src='tree_stump_01', objects=None, tris=3000, pivot='skirt', distance=160.0, grade=(0.08, 0.062, 0.042)),
    dict(name='deadwood_stump_02', prop=True, src='tree_stump_02', objects=None, tris=3000, pivot='skirt', distance=160.0, grade=(0.08, 0.062, 0.042)),
    dict(name='deadwood_branch_01', src='dry_branches_medium_01', objects=['dry_branches_medium_01_a'], tris=1200, pivot='ground', distance=90.0, grade=WOOD),
    dict(name='deadwood_branch_02', src='dry_branches_medium_01', objects=['dry_branches_medium_01_b'], tris=1000, pivot='ground', distance=90.0, grade=WOOD),
    dict(name='deadwood_branch_03', src='dry_branches_medium_01', objects=['dry_branches_medium_01_c'], tris=900, pivot='ground', distance=90.0, grade=WOOD),
]


def import_scan(folder, names):
    eg.reset_scene()
    gltf = [f for f in os.listdir(folder) if f.endswith('.gltf')][0]
    bpy.ops.import_scene.gltf(filepath=os.path.join(folder, gltf))
    meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH' and (names is None or o.name in names)]
    for o in list(bpy.context.scene.objects):
        if o not in meshes and o.type == 'MESH':
            bpy.data.objects.remove(o, do_unlink=True)
    bpy.ops.object.select_all(action='DESELECT')
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    high = bpy.context.view_layer.objects.active
    # корень без родителя и поворота: координаты — в меше
    bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return high


def place_on_ground(obj, pivot):
    """Середина по X/Y — в начало; по Z — опора на землю (см. шапку)."""
    me = obj.data
    co = np.array([v.co[:] for v in me.vertices], np.float64)
    centre = (co.min(0) + co.max(0)) * 0.5
    if pivot == 'skirt':
        # край бугра — вершины на рёбрах с одной гранью
        edge_faces = {}
        for p in me.polygons:
            for key in p.edge_keys:
                edge_faces[key] = edge_faces.get(key, 0) + 1
        border = {i for key, n in edge_faces.items() if n == 1 for i in key}
        ground = float(np.mean(co[list(border), 2])) if border else float(np.percentile(co[:, 2], 2))
        ground += 0.05
    else:
        ground = float(np.percentile(co[:, 2], 2)) + 0.02
    shift = np.array([centre[0], centre[1], ground])
    for v in me.vertices:
        v.co = (v.co[0] - shift[0], v.co[1] - shift[1], v.co[2] - shift[2])
    me.update()


def decimated_copy(high, target):
    low = high.copy()
    low.data = high.data.copy()
    low.name = high.name + '_low'
    bpy.context.scene.collection.objects.link(low)
    tris = sum(len(p.vertices) - 2 for p in high.data.polygons)
    if tris > target:
        mod = low.modifiers.new('decimate', 'DECIMATE')
        mod.decimate_type = 'COLLAPSE'
        mod.ratio = target / tris
        mod.use_collapse_triangulate = True
        bpy.ops.object.select_all(action='DESELECT')
        low.select_set(True)
        bpy.context.view_layer.objects.active = low
        bpy.ops.object.modifier_apply(modifier=mod.name)
    if getattr(low.data, 'has_custom_normals', False):
        bpy.context.view_layer.objects.active = low
        bpy.ops.mesh.customdata_custom_splitnormals_clear()
    for p in low.data.polygons:
        p.use_smooth = True
    # без ветра: NatureManufacture умножает силу ветра на альфу цвета вершин
    colour = low.data.color_attributes.new('Col', 'BYTE_COLOR', 'CORNER')
    colour.data.foreach_set('color', np.tile(np.array([1.0, 1.0, 1.0, 0.0], np.float32), len(colour.data)))
    return low


def bake_normal(high, low, path, extrusion, distance):
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.samples = 4
    bake = scene.render.bake
    bake.use_selected_to_active = True
    bake.cage_extrusion = extrusion
    bake.max_ray_distance = distance
    bake.margin = 8
    bake.normal_space = 'TANGENT'
    image = bpy.data.images.new(os.path.basename(path), TEXTURE, TEXTURE, alpha=False, float_buffer=False)
    image.colorspace_settings.name = 'Non-Color'
    material = bpy.data.materials.new(low.name + '_bake')
    material.use_nodes = True
    node = material.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = image
    material.node_tree.nodes.active = node
    low.data.materials.clear()
    low.data.materials.append(material)
    bpy.ops.object.select_all(action='DESELECT')
    high.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low
    bpy.ops.object.bake(type='NORMAL')
    image.filepath_raw = path
    image.file_format = 'PNG'
    image.save()
    return path


def scan_texture(folder, suffix):
    textures = os.path.join(folder, 'textures')
    for f in os.listdir(textures):
        if f.endswith(suffix + '_2k.jpg'):
            return os.path.join(textures, f)
    raise FileNotFoundError(folder + ' ' + suffix)


def pixels(path, data):
    image = bpy.data.images.load(path, check_existing=False)
    if data:
        image.colorspace_settings.name = 'Non-Color'
    image.scale(TEXTURE, TEXTURE)
    px = np.array(image.pixels[:], np.float32).reshape(TEXTURE, TEXTURE, 4)[::-1].copy()
    bpy.data.images.remove(image)
    return px


def write_textures(folder, out, name, grade):
    # альбедо: значения файла (sRGB) -> линейно -> в палитру сцены
    diff = pixels(scan_texture(folder, '_diff'), data=True)[..., :3]
    linear = np.where(diff <= 0.04045, diff / 12.92, ((diff + 0.055) / 1.055) ** 2.4)
    eg.save_png(eg.grade_albedo(linear, grade, GRADE_SATURATION), os.path.join(out, name + '_albedo.png'))
    arm = pixels(scan_texture(folder, '_arm'), data=True)
    mask = np.zeros((TEXTURE, TEXTURE, 4), np.float32)
    mask[..., 1] = arm[..., 0]
    mask[..., 2] = 0.5
    mask[..., 3] = 1.0 - arm[..., 1]
    eg.save_png(mask, os.path.join(out, name + '_mask.png'), linear=True)


def material_entry(name):
    return dict(id=name, role='bark', albedo=name + '_albedo.png', normal=name + '_normal.png', mask=name + '_mask.png',
                tiling=[1, 1], normalScale=1.0, smoothnessMax=1.0, aoMax=1.0, stiffness=1.0, drag=0.0001,
                initialBend=0.0, heightDrag=5.0)


def build(spec, src_root, out_root):
    folder = os.path.join(src_root, spec['src'])
    out = os.path.join(out_root, spec['name'])
    os.makedirs(out, exist_ok=True)
    high = import_scan(folder, spec['objects'])
    place_on_ground(high, spec['pivot'])
    low = decimated_copy(high, spec['tris'])
    size = max(high.dimensions)
    bake_normal(high, low, os.path.join(out, spec['name'] + '_normal.png'), extrusion=0.004 * size + 0.005,
                distance=0.01 * size + 0.02)
    write_textures(folder, out, spec['name'], spec['grade'])
    bpy.data.objects.remove(high, do_unlink=True)
    low.name = spec['name']
    fbx = spec['name'] + '.fbx'
    eg.export_fbx([low], os.path.join(out, fbx))
    co = np.array([v.co[:] for v in low.data.vertices], np.float32)
    tris = sum(len(p.vertices) - 2 for p in low.data.polygons)
    manifest = dict(name=spec['name'], type='detail', group=GROUP, height=float(co[:, 2].max()), trunkRadius=0.0,
                    colliderHeight=0.0, evergreen=False,
                    lods=[dict(fbx=fbx, screen=0.0, submeshes=[spec['name']], castShadows=True, triangles=int(tris))],
                    materials=[material_entry(spec['name'])],
                    nr=dict(renderDistance=spec['distance'], shadows=True, densityInDistance=1.0),
                    size=[round(float(x), 3) for x in co.max(0) - co.min(0)], triangles=int(tris),
                    source='Poly Haven ' + spec['src'] + ' (CC0)')
    with open(os.path.join(out, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(manifest, f, indent=1)
    if spec.get('prop'):
        eg.write_prop_twin(out, manifest)
    print('DEADWOOD_ASSET', spec['name'], json.dumps(dict(triangles=int(tris), size=manifest['size'])))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--src', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--assets', nargs='*')
    args = parser.parse_args(eg.script_args())
    for spec in ASSETS:
        if args.assets and spec['name'] not in args.assets:
            continue
        build(spec, args.src, args.out)


main()
