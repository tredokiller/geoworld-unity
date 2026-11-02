# Маски текстур земли (Environment/Textures/Ground пакета) для смешивания со спутником (GroundDetail): по набору — <имя>_mask.jpg,
# R — высота (растянута по 1–99 перцентилям, чтобы смешивание по высоте шло одинаково у всех наборов), G — AO, B — гладкость
# (1 − шероховатость; где её нет — DEFAULT_SMOOTHNESS). JPG, потому что LFS проекта ведёт из картинок только JPG; без
# прореживания цвета (4:4:4) — каналы независимы, иначе высота растекалась бы в AO и гладкость.
# Лиственную подстилку NatureManufacture (TGA) кладёт рядом обычным набором (_diff, _nor_gl, _mask).
# Исходники (_disp, _ao, _rough) лежат в проекте игры (Assets/_Game/Textures/Ground), маски пишутся в пакет рядом с _diff.
# Запуск: py -I Tools~/GroundTextures/pack_ground_masks.py <корень проекта> [набор ...] — с именами наборов пакует только их.
import os
import sys

from PIL import Image

DEFAULT_SMOOTHNESS = 0.1
PACKAGE_GROUND = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Environment", "Textures", "Ground"))
NM_LEAVES = ("Assets/Plugins/NatureManufacture Assets/Meadow Environment Dynamic Nature/Ground", "T_forest_ground_leaves_01")


def stretch(height):
    histogram = height.histogram()
    total = sum(histogram)
    low = high = 0
    seen = 0
    for value, count in enumerate(histogram):
        seen += count
        if seen <= total * 0.01:
            low = value
        if seen <= total * 0.99:
            high = value
    high = max(high, low + 1)
    return height.point(lambda v: max(0, min(255, round((v - low) * 255 / (high - low)))))


def gray(path):
    image = Image.open(path)
    if image.mode in ("I;16", "I;16B", "I"):
        image = image.point(lambda v: v / 256)
    return image.convert("L")


def save_mask(folder, name, height, ao, smoothness):
    size = height.size
    ao = ao.resize(size) if ao is not None else Image.new("L", size, 255)
    smoothness = smoothness.resize(size) if smoothness is not None else Image.new("L", size, round(DEFAULT_SMOOTHNESS * 255))
    mask = Image.merge("RGB", (stretch(height), ao, smoothness))
    mask.save(os.path.join(folder, name + "_mask.jpg"), quality=95, subsampling=0)


def pack_sets(ground, out, only=None):
    packed = 0
    for group in sorted(os.listdir(ground)):
        group_path = os.path.join(ground, group)
        if not os.path.isdir(group_path):
            continue
        for name in sorted(os.listdir(group_path)):
            if only and name not in only:
                continue
            folder = os.path.join(group_path, name)
            disp = os.path.join(folder, name + "_disp.jpg")
            if not os.path.isfile(disp):
                continue
            ao = os.path.join(folder, name + "_ao.jpg")
            rough = os.path.join(folder, name + "_rough.jpg")
            smoothness = Image.eval(gray(rough), lambda v: 255 - v) if os.path.isfile(rough) else None
            target = os.path.join(out, group, name)
            os.makedirs(target, exist_ok=True)
            save_mask(target, name, gray(disp), gray(ao) if os.path.isfile(ao) else None, smoothness)
            packed += 1
    return packed


def stage_leaves(project, out):
    source, prefix = NM_LEAVES
    source = os.path.join(project, source)
    folder = os.path.join(out, "NatureManufacture", "forest_leaves")
    os.makedirs(folder, exist_ok=True)
    albedo = Image.open(os.path.join(source, prefix + "_A_SM.tga")).convert("RGBA")
    albedo.convert("RGB").save(os.path.join(folder, "forest_leaves_diff.jpg"), quality=95)
    Image.open(os.path.join(source, prefix + "_N.png")).convert("RGB").save(os.path.join(folder, "forest_leaves_nor_gl.jpg"), quality=97)
    # маска NatureManufacture: R — металличность, G — AO, B — высота, A — гладкость
    _, ao, height, smoothness = Image.open(os.path.join(source, prefix + "_AO_H_SM.tga")).convert("RGBA").split()
    save_mask(folder, "forest_leaves", height, ao, smoothness)


def main():
    project = sys.argv[1]
    ground = os.path.join(project, "Assets", "_Game", "Textures", "Ground")
    only = set(sys.argv[2:])
    if only:
        print("masks", pack_sets(ground, PACKAGE_GROUND, only))
        return
    stage_leaves(project, PACKAGE_GROUND)
    print("masks", pack_sets(ground, PACKAGE_GROUND) + 1)


main()
