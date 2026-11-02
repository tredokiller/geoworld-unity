# Ужатие PNG в папках ассетов сосны без потерь: карты без альфы (кора — альбедо и нормаль, нормали хвои и креста)
# пишутся RGB вместо RGBA, всё — с оптимизацией zlib. Маски (A — гладкость) и альбедо карточек (A — вырез) — RGBA.
# Запуск системным Python с Pillow:  py pine_pack.py <папка pine>
import json
import os
import sys

from PIL import Image


def pack(folder):
    with open(os.path.join(folder, 'manifest.json')) as f:
        man = json.load(f)
    rgb = set()
    for m in man['materials']:
        if m.get('normal'):
            rgb.add(m['normal'])
        if m['role'] == 'bark':
            rgb.add(m['albedo'])
    before = after = 0
    for name in sorted(os.listdir(folder)):
        if not name.endswith('.png'):
            continue
        path = os.path.join(folder, name)
        before += os.path.getsize(path)
        im = Image.open(path)
        im.load()
        if name in rgb and im.mode == 'RGBA':
            im = im.convert('RGB')
        im.save(path, optimize=True)
        after += os.path.getsize(path)
    return before, after


if __name__ == '__main__':
    root = sys.argv[1]
    for d in sorted(os.listdir(root)):
        folder = os.path.join(root, d)
        if os.path.isfile(os.path.join(folder, 'manifest.json')):
            b, a = pack(folder)
            print(f'{d}: {b / 1e6:.1f} MB -> {a / 1e6:.1f} MB')
