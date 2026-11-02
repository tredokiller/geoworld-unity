# Поле и сорняки (Tools/TreeGen), спецификация — план размещения, раздел 3 (N1–N7), правки — ревью второго прохода.
# Как трава (grass_gen.py): форма — геометрией (стебли, листья-полигоны), цвет — из атласа полос 256² без альфы, вес
# ветра — альфа вершин, нормали смешаны с «вверх». Все ассеты группы Field делят один атлас field_set_* (импортёр кладёт
# текстуры группы одним файлом; старый field_atlas_* остаётся у прежних префабов). Атлас — 4 ряда по 16 полос (16×64 px):
# у тонких стеблей вдали мип выбирается по длине, цвет полосы держится до мипа 4; пустые места ряда — копии его же полос
# (в дальних мипах цвет не смешивается с чужим).
# Пятна-ковры (стерня, всходы) — с плоской плотностью: ровно внутри, мягкий спад только у края (лопастные пятна с пиком в
# центре давали на 3 м острова и проплешины); размер пятна подобран под расстановку игры (2-метровые ячейки, 0.6–1
# экземпляр на м²), sizeVariation у них ≤ 0.1 (шейдер уменьшает весь экземпляр пятнами по 6 м — вместе с площадью).
#  - field_stubble_fresh_01/02 (N1): свежая стерня пшеницы — золотистые срезанные стебли 10–20 см по 1–3 от куста (без
#    рядов), серо-бурый низ, светлый срез, узел; ломаные и лежащие стебли; на земле — слой измельчённой соломы (пласты
#    из нескольких соломин, отдельные соломины, мелкая полова: ~30 % земли при плотности плана). Пятно ~2.3 м;
#  - field_stubble_aged_01 (N2): осенняя серо-бурая стерня (темнее свежей), 15 % сломаны или наклонены, слежавшаяся
#    солома, падалица в 2–3 полосах;
#  - field_sprouts_01 (N3): всходы озимой пшеницы в октябре — без рядов (у Nature Renderer каждый экземпляр повёрнут
#    случайно, ряды пятен перекрещивались штриховкой), россыпь с лёгкой кучностью, тёмная сизоватая зелень, 1–3 листа
#    (первый прямой, следующие дугой, через 180°); тонкая дымка, не газон. Материал — seasonKeep 1 (не желтеет осенью);
#  - field_weed_bindweed_01 (N4): вьюнок полевой (Convolvulus arvensis) — плети из корневой шейки во все стороны, по
#    земле; стреловидные листья разного размера на черешках, ушки назад; 6–9 воронок 2–2.5 см вверх, пятилопастных,
#    белых с розовыми полосами и жёлтым зевом;
#  - field_weed_goosefoot_01 (N5): марь белая (Chenopodium album) — кустистая (ширина ~половина высоты), зелёный стебель
#    с красными штрихами, ветви из нижних двух третей, ромбические зубчатые листья внизу, узкие вверху, бугристые
#    серо-мучнистые клубочки соцветий на верхушке, концах ветвей и в пазухах;
#  - field_weed_foxtail_01 (N6): щетинник (Setaria viridis / pumila) — пучок листьев, коленчатые соломины, плотные
#    щетинистые метёлки с «пушистым» краем, у зелёных (S. viridis) кончик поникает, жёлтые (30 %, S. pumila) прямые;
#  - edge_wormwood_01 (N7): полынь горькая (Artemisia absinthium) — серебристый полукустарник-холмик: розетка крупных
#    перисторассечённых листьев у земли, густые прямые стебли с мелкими рассечёнными листьями, наружные клонятся,
#    верхняя треть — метёлки мелких серо-жёлтых корзинок; прошлогодний сухой стебель.
# Мёртвым растениям (стерня, полынь холмика — зимой) смена цвета по сезону не нужна: seasonKeep 2 («не перекрашивать»;
# в SeasonFoliage.hlsl пока действует как 1 — цвет круглый год, зимой тусклее).
# Запуск: blender.exe -b --factory-startup --python field_gen.py -- <папка вывода> [имя ассета ...]
import json
import math
import os
import random
import sys
import zlib

import numpy as np
from mathutils import Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

UP = Vector((0, 0, 1))
ATLAS = 256
COLS, ROWS = 16, 4
BLOCK_W, BLOCK_H = ATLAS // COLS, ATLAS // ROWS
MARGIN = 3
ATLAS_NAME = 'field_set'

# Полосы: имя, [(t, sRGB)] от комля/основания (t=0) к верху/кончику (t=1), гладкость (A маски), AO у основания (G маски),
# доп.: nodes — тёмные полосы узлов [(t, полуширина, множитель)], stripes — продольные штрихи
# [(доля U, ширина, цвет, сила, t начала)] (полоса берётся поперёк всей ширины блока: suv(..., span=7))
# пласт соломы: три соломины разного тона, между ними тёмные щели (тень соломины на соседней), поперёк всего блока
STRAW_LINES = [(0.08, 0.1, '#5E4E36', 0.85, 0.0), (0.36, 0.09, '#5E4E36', 0.8, 0.0), (0.64, 0.09, '#5E4E36', 0.8, 0.0),
               (0.92, 0.1, '#5E4E36', 0.85, 0.0), (0.22, 0.16, '#E2CF9C', 0.35, 0.0), (0.5, 0.14, '#A88F60', 0.3, 0.0),
               (0.78, 0.16, '#DCC894', 0.3, 0.0)]
AGED_LINES = [(0.08, 0.1, '#3F3A31', 0.85, 0.0), (0.36, 0.09, '#3F3A31', 0.8, 0.0), (0.64, 0.09, '#3F3A31', 0.8, 0.0),
              (0.92, 0.1, '#3F3A31', 0.85, 0.0), (0.22, 0.16, '#A69B84', 0.35, 0.0), (0.5, 0.14, '#776D5C', 0.3, 0.0),
              (0.78, 0.16, '#9E937C', 0.3, 0.0)]
STRIP_ROWS = [
    [  # ряд 0 — свежая стерня: золотистая солома, серо-бурый низ (нижняя треть темнее), светлый срез
        ('stub', [(0, '#73684F'), (0.15, '#8C7C59'), (0.32, '#B8A068'), (0.45, '#CFB77C'), (0.9, '#D6C084'), (0.94, '#E8D8A6'),
                  (1, '#EBDDAC')], 0.4, 0.55, dict(nodes=[(0.3, 0.022, 0.84)])),
        ('stub_b', [(0, '#6E644C'), (0.15, '#887856'), (0.32, '#B39C66'), (0.45, '#CAB37A'), (0.9, '#CFB980'), (0.94, '#E3D3A2'),
                    (1, '#E6D7A8')], 0.4, 0.55, dict(nodes=[(0.38, 0.022, 0.84)])),
        ('stub_shade', [(0, '#5F5644'), (0.2, '#7A6C4E'), (0.4, '#9C8659'), (0.9, '#A89262'), (0.94, '#C8B585'), (1, '#CCB98A')],
         0.35, 0.55, dict(nodes=[(0.33, 0.022, 0.86)])),
        ('stub_broken', [(0, '#6E644C'), (0.3, '#AC935E'), (0.8, '#C4AA70'), (0.9, '#DCC890'), (1, '#E2CF98')], 0.4, 0.55, {}),
        ('stub_lodged', [(0, '#9A875E'), (0.25, '#BBA26C'), (0.85, '#C4AB74'), (1, '#CDB57E')], 0.4, 0.75, {}),
        ('chaff_a', [(0, '#C9B27C'), (0.5, '#D3BC82'), (1, '#D9C48D')], 0.35, 0.85, {}),
        ('chaff_b', [(0, '#B39D6A'), (0.5, '#BFA872'), (1, '#C6B07C')], 0.35, 0.85, {}),
        ('chaff_c', [(0, '#9B885D'), (0.5, '#A69264'), (1, '#AF9B6E')], 0.35, 0.85, {}),
        ('mulch_a', [(0, '#BDA97C'), (0.5, '#C8B485'), (1, '#CEBB8C')], 0.3, 0.9, dict(stripes=STRAW_LINES)),
        ('mulch_b', [(0, '#A99A74'), (0.5, '#B4A47E'), (1, '#BBAB85')], 0.3, 0.9, dict(stripes=STRAW_LINES)),
        ('husk', [(0, '#CDB884'), (0.5, '#D9C690'), (1, '#E0CE9A')], 0.3, 0.9, {}),
    ],
    [  # ряд 1 — осенняя стерня (серо-бурая, темнее свежей) и падалица
        ('aged', [(0, '#514A3F'), (0.25, '#786E5C'), (0.4, '#8D826E'), (0.9, '#918573'), (0.94, '#A49A84'), (1, '#A89E88')],
         0.25, 0.55, dict(nodes=[(0.32, 0.022, 0.85)])),
        ('aged_b', [(0, '#4C463C'), (0.25, '#726959'), (0.4, '#877C69'), (0.9, '#8A7F6C'), (0.94, '#9D937C'), (1, '#A19781')],
         0.25, 0.55, dict(nodes=[(0.4, 0.022, 0.85)])),
        ('aged_shade', [(0, '#464137'), (0.3, '#635B4D'), (0.45, '#6F6657'), (0.9, '#746B5C'), (0.94, '#857C6A'), (1, '#898070')],
         0.2, 0.55, {}),
        ('aged_broken', [(0, '#4C463C'), (0.3, '#807663'), (0.85, '#887D6A'), (1, '#9C927C')], 0.25, 0.55, {}),
        ('aged_chaff_a', [(0, '#7F7662'), (0.5, '#867C68'), (1, '#8C836E')], 0.2, 0.85, {}),
        ('aged_chaff_b', [(0, '#716857'), (0.5, '#776E5C'), (1, '#7E7462')], 0.2, 0.85, {}),
        ('aged_chaff_c', [(0, '#635B4C'), (0.5, '#686051'), (1, '#6E6657')], 0.2, 0.85, {}),
        ('aged_mulch', [(0, '#776D5A'), (0.5, '#807663'), (1, '#867C69')], 0.2, 0.9, dict(stripes=AGED_LINES)),
        ('volunteer', [(0, '#3E5A2A'), (0.45, '#557A38'), (1, '#62853F')], 0.5, 0.7, {}),
        ('volunteer_b', [(0, '#3A5628'), (0.45, '#4E7136'), (1, '#5C7D3E')], 0.5, 0.7, {}),
    ],
    [  # ряд 2 — зелень: всходы (тёмная сизоватая озимь), щетинник, вьюнок
        ('sprout', [(0, '#3C5728'), (0.45, '#4E6E34'), (1, '#5F803F')], 0.5, 0.7, {}),
        ('sprout_b', [(0, '#38522A'), (0.45, '#4A6A36'), (1, '#5A7B42')], 0.5, 0.7, {}),
        ('sprout_c', [(0, '#405A2B'), (0.45, '#537338'), (1, '#658645')], 0.5, 0.7, {}),
        ('fox_blade', [(0, '#4E6532'), (0.5, '#6E8A44'), (1, '#8AA05A')], 0.5, 0.7, {}),
        ('fox_blade_b', [(0, '#4A6030'), (0.5, '#67833F'), (1, '#839A55')], 0.5, 0.7, {}),
        ('fox_culm', [(0, '#596C3B'), (0.6, '#6E8A44'), (1, '#7A954E')], 0.45, 0.7, {}),
        ('fox_spike', [(0, '#5F7E3A'), (0.5, '#7A9A4A'), (1, '#86A456')], 0.3, 0.8, {}),
        ('fox_spike_y', [(0, '#9C8846'), (0.5, '#B9A050'), (1, '#C4AC5E')], 0.3, 0.8, {}),
        ('bw_runner', [(0, '#5F6F3E'), (0.5, '#6B7B45'), (1, '#72824B')], 0.5, 0.8, {}),
        ('bw_leaf', [(0, '#557337'), (0.5, '#5F7E3E'), (1, '#648341')], 0.65, 0.85, {}),
        ('bw_leaf_lit', [(0, '#6A8945'), (0.5, '#73914A'), (1, '#79964F')], 0.65, 0.85, {}),
        ('bw_leaf_young', [(0, '#6E8C48'), (0.5, '#7B9852'), (1, '#83A05A')], 0.6, 0.9, {}),
        ('bw_flower', [(0, '#C9C46E'), (0.2, '#E4DFB2'), (0.4, '#F3EBE8'), (0.86, '#F2E8E8'), (1, '#EBD2D9')], 0.35, 1.0,
         dict(stripes=[(0.5, 0.3, '#D48CA7', 0.9, 0.28)])),
        ('bw_flower_pink', [(0, '#C9C46E'), (0.2, '#E2D2B8'), (0.4, '#EED3DC'), (0.86, '#EBCAD5'), (1, '#E3B5C6')], 0.35, 1.0,
         dict(stripes=[(0.5, 0.3, '#C8789A', 0.9, 0.28)])),
    ],
    [  # ряд 3 — марь и полынь
        ('gf_stem', [(0, '#6A4E3C'), (0.08, '#5E6B3C'), (0.3, '#6E7D46'), (0.7, '#76854C'), (1, '#7F8E55')], 0.3, 0.75,
         dict(stripes=[(0.25, 0.14, '#8A4A3C', 0.7, 0.0), (0.7, 0.1, '#8A4A3C', 0.55, 0.0)])),
        ('gf_leaf', [(0, '#5F7053'), (0.5, '#6C7E60'), (1, '#748667')], 0.2, 0.85, {}),
        ('gf_leaf_young', [(0, '#87937A'), (0.5, '#97A387'), (1, '#9FAA90')], 0.12, 0.9, {}),
        ('gf_mealy', [(0, '#69755C'), (0.5, '#78846A'), (1, '#808C72')], 0.08, 0.75,
         dict(speckle=dict(colour='#A8B09A', share=0.45))),
        ('gf_mealy_g', [(0, '#5E6B50'), (0.5, '#6C7A5D'), (1, '#748263')], 0.1, 0.75,
         dict(speckle=dict(colour='#97A287', share=0.4))),
        ('ww_leaf', [(0, '#868D78'), (0.5, '#8F957F'), (1, '#959B85')], 0.15, 0.75,
         dict(pinnate=dict(dark='#5E6553', lobes=6, slope=0.6))),
        ('ww_leaf_lit', [(0, '#949A85'), (0.5, '#9DA38D'), (1, '#A3A893')], 0.15, 0.75,
         dict(pinnate=dict(dark='#6A715E', lobes=6, slope=0.6))),
        ('ww_leaf_in', [(0, '#6E7462'), (0.5, '#767C69'), (1, '#7C826E')], 0.12, 0.65,
         dict(pinnate=dict(dark='#4B5243', lobes=6, slope=0.6))),
        ('ww_stem', [(0, '#7C8070'), (0.4, '#878D7A'), (1, '#8E9581')], 0.15, 0.75, {}),
        ('ww_wood', [(0, '#5E5A4C'), (0.5, '#6F6A5A'), (1, '#7C7866')], 0.1, 0.6, {}),
        ('ww_panicle', [(0, '#6F7563'), (0.5, '#7C826D'), (1, '#858A73')], 0.15, 0.85,
         dict(speckle=dict(colour='#B4AB7E', share=0.5, strength=0.8))),
        ('ww_dry', [(0, '#7A7262'), (0.5, '#8C8270'), (1, '#968C7A')], 0.12, 0.75, {}),
    ],
]
STRIPS = {}
ROW_USED = []
for _row, _items in enumerate(STRIP_ROWS):
    assert len(_items) <= COLS
    ROW_USED.append(len(_items))
    for _col, _item in enumerate(_items):
        STRIPS[_item[0]] = (_row * COLS + _col, _item)


def suv(name, t, side=0.0, span=2.0):
    """UV полосы: t — вдоль (0 комель, 1 верх), side −1..1 поперёк; span — полуширина в пикселях (узкая — мип по длине)."""
    index = STRIPS[name][0]
    col, row = index % COLS, index // COLS
    u = (col * BLOCK_W + BLOCK_W * 0.5 + side * span) / ATLAS
    v = (row * BLOCK_H + MARGIN + min(1.0, max(0.0, t)) * (BLOCK_H - 2 * MARGIN)) / ATLAS
    return (u, v)


def build_atlas(out_dir):
    rng = np.random.default_rng(11)
    albedo = np.zeros((ATLAS, ATLAS, 3), np.float32)
    mask = np.zeros((ATLAS, ATLAS, 4), np.float32)
    mask[..., 1] = 1.0
    mask[..., 3] = 0.3
    t = np.clip((np.arange(BLOCK_H) + 0.5 - MARGIN) / (BLOCK_H - 2 * MARGIN), 0, 1)
    ufrac = (np.arange(BLOCK_W) + 0.5) / BLOCK_W
    blocks = {}
    for index, (name, stops, smooth_, ao, extra) in STRIPS.values():
        ts = [s[0] for s in stops]
        cs = np.array([eg.hex_rgb(s[1]) for s in stops], np.float32)
        colour = np.stack([np.interp(t, ts, cs[:, k]) for k in range(3)], axis=1)  # (BLOCK_H, 3), t снизу вверх
        block = np.repeat(colour[:, None, :], BLOCK_W, axis=1)
        for tn, half, mul in extra.get('nodes', []):
            band = np.clip(1 - np.abs(t - tn) / half, 0, 1)
            block *= (1 - (1 - mul) * band)[:, None, None]
        for uc, width, hexc, strength, t0 in extra.get('stripes', []):
            k = np.clip(1 - np.abs(ufrac - uc) / (width * 0.5), 0, 1) ** 0.7 * strength
            kt = np.clip((t - t0) / 0.15, 0, 1) if t0 > 0 else np.ones_like(t)
            kk = kt[:, None, None] * k[None, :, None]
            block = block * (1 - kk) + np.array(eg.hex_rgb(hexc), np.float32)[None, None, :] * kk
        if 'pinnate' in extra:
            # перисторассечённый лист в полосе: светлый рахис по середине, доли — косые светлые полосы вперёд от рахиса,
            # сужаются к краю; между ними — тень (тёмный цвет). Лист-полигон берёт всю ширину блока (span 7)
            pin = extra['pinnate']
            xs = ((ufrac - 0.5) / 0.4375)[None, :]
            ax = np.abs(xs)
            # доли очерёдные (сдвиг фазы на правой стороне), край доли волнистый (вторичные лопасти)
            phase = np.mod(t[:, None] * pin['lobes'] - ax * pin['slope'] * pin['lobes'] * 0.5 + (xs > 0) * 0.5, 1.0)
            width = 0.56 * (1 - 0.5 * ax) + 0.09 * np.sin(ax * 17.0 + t[:, None] * 31.0)
            lobe = np.clip((width - phase) * 7.0, 0, 1) * np.clip(phase * 9.0, 0, 1)
            lobe *= np.clip((0.9 - ax) * 7.0, 0, 1) * np.clip((t[:, None] - 0.06) * 12.0, 0, 1)
            rachis = np.clip((0.1 - ax) * 14.0, 0, 1)
            leaf = np.maximum(lobe, rachis)
            dark = np.array(eg.hex_rgb(pin['dark']), np.float32)
            block = dark[None, None, :] + (block - dark[None, None, :]) * leaf[..., None]
        if 'speckle' in extra:
            # мучнистые клубочки: зёрна-крупинки 1–2 пикселя светлого налёта на более тёмной зелени
            sp = extra['speckle']
            grain = rng.random((BLOCK_H // 2 + 1, BLOCK_W // 2 + 1)).repeat(2, 0).repeat(2, 1)[:BLOCK_H, :BLOCK_W]
            k = (grain < sp['share']).astype(np.float32)[..., None] * sp.get('strength', 0.8)
            block = block * (1 - k) + np.array(eg.hex_rgb(sp['colour']), np.float32)[None, None, :] * k
        block *= (1 + (rng.random((BLOCK_H, 1, 1)) - 0.5) * 0.05) * (1 + (rng.random((1, BLOCK_W, 1)) - 0.5) * 0.06)
        block *= 1 + (rng.random((BLOCK_H, BLOCK_W, 1)) - 0.5) * 0.04
        ao_col = (ao + (1 - ao) * np.sqrt(t))[:, None]
        blocks[index] = (block, ao_col, smooth_)
    for row in range(ROWS):
        used = ROW_USED[row] if row < len(ROW_USED) else 0
        for col in range(COLS):
            if not used:
                continue
            block, ao_col, smooth_ = blocks[row * COLS + (col if col < used else col % used)]
            y0 = ATLAS - (row + 1) * BLOCK_H  # строка картинки верха блока (картинка сверху вниз, v снизу вверх)
            x0 = col * BLOCK_W
            albedo[y0:y0 + BLOCK_H, x0:x0 + BLOCK_W] = block[::-1]
            mask[y0:y0 + BLOCK_H, x0:x0 + BLOCK_W, 1] = ao_col[::-1]
            mask[y0:y0 + BLOCK_H, x0:x0 + BLOCK_W, 3] = smooth_
    normal = np.zeros((ATLAS, ATLAS, 3), np.float32) + np.array([0.5, 0.5, 1.0], np.float32)
    eg.save_png(albedo, os.path.join(out_dir, ATLAS_NAME + '_albedo.png'))
    eg.save_png(normal, os.path.join(out_dir, ATLAS_NAME + '_normal.png'), linear=True)
    eg.save_png(mask, os.path.join(out_dir, ATLAS_NAME + '_mask.png'), linear=True)


# ------------------------------------------------------------------ геометрия

def wcol(a):
    return (1.0, 1.0, 1.0, max(0.0, min(1.0, a)))


def mix_up(n, k):
    v = n * (1 - k) + UP * k
    return v.normalized() if v.length > 1e-6 else UP.copy()


def smooth(a, b, v):
    t = min(1.0, max(0.0, (v - a) / (b - a)))
    return t * t * (3 - 2 * t)


def make_flat_blob(rng, radius, lobes=2, harmonic=0.12, ripple=1.0):
    """Пятно для ковра: 1–2 лопасти с неровным краем (гармоники 2, 3, 5 по углу у каждой). blob — 1 − d/r: 0 на краю,
    1 в центре лопасти; плотность снаружи берётся через smooth(lo, feather, blob) — плоская внутри, спад у края."""
    parts = []
    if lobes <= 1:
        parts.append((0.0, 0.0, radius))
    else:
        parts.append((0.0, 0.0, 0.84 * radius))
        for _ in range(lobes - 1):
            a = rng.uniform(0, math.tau)
            d = rng.uniform(0.18, 0.32) * radius
            parts.append((d * math.cos(a), d * math.sin(a), rng.uniform(0.6, 0.72) * radius))
    parts = [(cx, cy, r, [rng.uniform(0, math.tau) for _ in range(3)]) for cx, cy, r in parts]
    w = [rng.uniform(0, math.tau) for _ in range(4)]
    k = 1.0 / radius

    def blob(x, y):
        best = -9.0
        for cx, cy, r, ph in parts:
            dx, dy = x - cx, y - cy
            th = math.atan2(dy, dx)
            rr = r * (1 + harmonic * (0.55 * math.sin(2 * th + ph[0]) + 0.3 * math.sin(3 * th + ph[1]) + 0.15 * math.sin(5 * th + ph[2])))
            best = max(best, 1.0 - math.hypot(dx, dy) / rr)
        return best + ripple * (0.05 * math.sin(7 * k * x + w[0]) * math.sin(6 * k * y + w[1]) +
                                0.03 * math.sin(15 * k * x + w[2]) * math.sin(13 * k * y + w[3]))
    extent = max(math.hypot(cx, cy) + r for cx, cy, r, _ in parts) * (1 + harmonic) * 1.12
    return blob, extent


def value_noise(rng, cell):
    """Плавный шум 0..1 с ячейкой cell (м) — кучность всходов."""
    grid = [[rng.random() for _ in range(64)] for _ in range(64)]

    def noise(x, y):
        fx, fy = x / cell + 32.0, y / cell + 32.0
        ix, iy = int(math.floor(fx)), int(math.floor(fy))
        tx, ty = fx - ix, fy - iy
        tx, ty = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty)
        a, b = grid[ix % 64][iy % 64], grid[(ix + 1) % 64][iy % 64]
        c, d = grid[ix % 64][(iy + 1) % 64], grid[(ix + 1) % 64][(iy + 1) % 64]
        return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty
    return noise


class Grid:
    def __init__(self, cell):
        self.cell = cell
        self.cells = {}

    def key(self, x, y):
        return int(math.floor(x / self.cell)), int(math.floor(y / self.cell))

    def near(self, x, y, r):
        cx, cy = self.key(x, y)
        for i in range(cx - 1, cx + 2):
            for j in range(cy - 1, cy + 2):
                for px, py in self.cells.get((i, j), ()):
                    if (px - x) ** 2 + (py - y) ** 2 < r * r:
                        return True
        return False

    def add(self, x, y):
        self.cells.setdefault(self.key(x, y), []).append((x, y))


def scatter(rng, count, blob, extent, feather, min_dist=0.0, lo=-0.05, density=None, halo=None):
    """Точки пятна: вероятность smooth(lo, feather, blob) (· density(x, y)) — ровно внутри, спад у края, редкие одиночки
    за краем; шаг ≥ min_dist. halo = (доля, масштаб): доля точек — в редком ореоле (то же пятно, увеличенное в масштаб
    раз, с мягким краем): там, где соседние экземпляры легли реже, ореолы заполняют проплешину. Возвращает (x, y, inner)."""
    points = []
    grid = Grid(max(min_dist, 0.01))
    tries = 0
    while len(points) < count and tries < count * 300:
        tries += 1
        if halo and rng.random() < halo[0]:
            sc = halo[1]
            x, y = rng.uniform(-extent * sc, extent * sc), rng.uniform(-extent * sc, extent * sc)
            inner = smooth(lo, 0.5, blob(x / sc, y / sc))
        else:
            x, y = rng.uniform(-extent, extent), rng.uniform(-extent, extent)
            inner = smooth(lo, feather, blob(x, y))
        if inner <= 0.0:
            continue
        if rng.random() > inner * (density(x, y) if density else 1.0):
            continue
        if min_dist > 0 and grid.near(x, y, min_dist):
            continue
        grid.add(x, y)
        points.append((x, y, inner))
    return points


def quad(mb, normals, verts, uvs, alphas, nrms):
    mb.face([mb.vert(v) for v in verts], 0, list(uvs), None, [wcol(a) for a in alphas])
    normals.extend(nrms)


def tri(mb, normals, verts, uvs, alphas, nrms):
    mb.face([mb.vert(v) for v in verts], 0, list(uvs), None, [wcol(a) for a in alphas])
    normals.extend(nrms)


def stalk(mb, normals, rng, base, top, width, strip, alpha_top, normal_up=0.55, sweep=55.0, t0=0.0, t1=1.0):
    """Стебель стерни — четырёхугольник со случайным разворотом. Нормали края повёрнуты на ±sweep от грани (как у
    цилиндра: одна сторона стебля на свету, другая в тени), потом смешаны с «вверх» на normal_up; изнанка берёт те же."""
    axis = (top - base).normalized()
    yaw = rng.uniform(0, math.tau)
    side = Vector((-math.sin(yaw), math.cos(yaw), 0))
    side = side - axis * side.dot(axis)
    if side.length < 1e-5:
        side = axis.orthogonal()
    side.normalize()
    face = axis.cross(side).normalized() * rng.choice((-1, 1))
    c, s_ = math.cos(math.radians(sweep)), math.sin(math.radians(sweep))
    nl = mix_up(face * c - side * s_, normal_up)
    nr = mix_up(face * c + side * s_, normal_up)
    s = side * width * 0.5
    quad(mb, normals, (base - s, base + s, top + s, top - s),
         (suv(strip, t0, -1), suv(strip, t0, 1), suv(strip, t1, 1), suv(strip, t1, -1)),
         (0.0, 0.0, alpha_top, alpha_top), (nl, nr, nr, nl))


def flat_piece(mb, normals, centre, yaw, length, width, z, rise, strip, alpha=0.02, normal_up=0.88):
    """Лежащая соломина / полова: плоская полоска на высоте z, один конец приподнят на rise."""
    d = Vector((math.cos(yaw), math.sin(yaw), 0))
    s = Vector((-d.y, d.x, 0)) * width * 0.5
    p0 = centre - d * length * 0.5 + Vector((0, 0, z))
    p1 = centre + d * length * 0.5 + Vector((0, 0, z + rise))
    face = (p1 - p0).normalized().cross(s.normalized())
    if face.z < 0:
        face = -face
    n = mix_up(face, normal_up)
    quad(mb, normals, (p0 - s, p0 + s, p1 + s, p1 - s),
         (suv(strip, 0.12, -1), suv(strip, 0.12, 1), suv(strip, 0.88, 1), suv(strip, 0.88, -1)),
         (alpha, alpha, alpha, alpha), (n, n, n, n))


def mat_piece(mb, normals, rng, centre, length, width, z, strip):
    """Пласт измельчённой соломы: 2–4 соломины рядом (продольные штрихи полосы) — неровный четырёхугольник, сужен и
    скошен к одному концу, конец чуть приподнят. Главная примета убранного поля сверху, где стебли видны торцом."""
    yaw = rng.uniform(0, math.tau)
    d = Vector((math.cos(yaw), math.sin(yaw), 0))
    s = Vector((-d.y, d.x, 0))
    w0 = width * rng.uniform(0.7, 1.0) * 0.5
    w1 = width * rng.uniform(0.35, 0.85) * 0.5
    skew = rng.uniform(-0.35, 0.35) * width
    rise = rng.uniform(0.0, 0.008)
    p0 = centre - d * length * 0.5 + Vector((0, 0, z))
    p1 = centre + d * length * 0.5 + s * skew + Vector((0, 0, z + rise))
    tilt = Vector((rng.gauss(0, 0.08), rng.gauss(0, 0.08), 1)).normalized()
    n = mix_up(tilt, 0.85)
    t0 = rng.uniform(0.05, 0.4)
    quad(mb, normals, (p0 - s * w0, p0 + s * w0, p1 + s * w1, p1 - s * w1),
         (suv(strip, t0, -1, 7), suv(strip, t0, 1, 7), suv(strip, t0 + 0.5, 1, 7), suv(strip, t0 + 0.5, -1, 7)),
         (0.01, 0.01, 0.02, 0.02), (n, n, n, n))


def husk(mb, normals, rng, centre, size, z, strip):
    """Полова: чешуйка-треугольник 1–2 см на земле."""
    a = rng.uniform(0, math.tau)
    pts = [centre + Vector((math.cos(a + k * 2.2 + rng.uniform(-0.3, 0.3)) * size * (0.6 if k else 1.0),
                            math.sin(a + k * 2.2 + rng.uniform(-0.3, 0.3)) * size * (0.6 if k else 1.0), z + rng.uniform(0, 0.003)))
           for k in range(3)]
    n = mix_up(Vector((rng.gauss(0, 0.1), rng.gauss(0, 0.1), 1)).normalized(), 0.85)
    tri(mb, normals, pts, (suv(strip, 1.0), suv(strip, 0.2, -1), suv(strip, 0.2, 1)), (0.01, 0.01, 0.01), (n, n, n))


def blade(mb, normals, root, length, width, tilt, droop, yaw, strip, segments=2, normal_up=0.65, alpha_max=0.5,
          alpha_h=0.25, width_power=2.0, twist=0.0):
    """Лист злака: звенья с нарастающим изгибом (tilt у основания + droop к кончику), острый кончик. segments=1 — один
    треугольник (прямой первый лист всхода)."""
    direction = Vector((math.cos(yaw), math.sin(yaw), 0))
    side0 = Vector((-direction.y, direction.x, 0))
    stops = [i / segments for i in range(segments + 1)]
    pos = Vector(root)
    points = [pos.copy()]
    for a, b in zip(stops[:-1], stops[1:]):
        angle = tilt + droop * ((a + b) * 0.5) ** 1.5
        pos = pos + (UP * math.cos(angle) + direction * math.sin(angle)) * (length * (b - a))
        points.append(pos.copy())
    rows = []
    for i, (t, p) in enumerate(zip(stops, points)):
        tangent = (points[min(i + 1, segments)] - points[max(i - 1, 0)]).normalized()
        side = side0.copy()
        side.rotate(Quaternion(tangent, twist * t))
        face = tangent.cross(side).normalized()
        if face.z < 0:
            face = -face
        a = min(1.0, (max(p.z - root[2], 0.0) / alpha_h) ** 1.5) * alpha_max
        if i < segments:
            half = width * 0.5 * (1 - t ** width_power) if i else width * 0.5
            nl, nr = face.copy(), face.copy()
            nl.rotate(Quaternion(tangent, math.radians(25)))
            nr.rotate(Quaternion(tangent, math.radians(-25)))
            rows.append((mb.vert(p - side * half), mb.vert(p + side * half), t, a, mix_up(nl, normal_up), mix_up(nr, normal_up)))
        else:
            rows.append((mb.vert(p), None, t, a, mix_up(face, normal_up + 0.1), None))
    for r0, r1 in zip(rows[:-1], rows[1:]):
        a0, a1, t0, c0, n0l, n0r = r0
        b0, b1, t1, c1, n1l, n1r = r1
        if b1 is None:
            mb.face([a0, a1, b0], 0, [suv(strip, t0, -1), suv(strip, t0, 1), suv(strip, t1)], None, [wcol(c0), wcol(c0), wcol(c1)])
            normals.extend([n0l, n0r, n1l])
        else:
            mb.face([a0, a1, b1, b0], 0, [suv(strip, t0, -1), suv(strip, t0, 1), suv(strip, t1, 1), suv(strip, t1, -1)], None,
                    [wcol(c0), wcol(c0), wcol(c1), wcol(c1)])
            normals.extend([n0l, n0r, n1r, n1l])


def ribbon(mb, normals, points, widths, strip, alphas, side_hint, normal_up=0.5, t_range=(0.0, 1.0), crossed=False, span=2.0):
    """Полоска вдоль ломаной (плеть, соломина, веточка); crossed — две полоски крест-накрест."""
    for k in range(2 if crossed else 1):
        hint = None
        if side_hint is not None:
            hint = side_hint if k == 0 else side_hint.cross((points[-1] - points[0]).normalized())
        rows = []
        n_pts = len(points)
        for i, p in enumerate(points):
            tangent = (points[min(i + 1, n_pts - 1)] - points[max(i - 1, 0)]).normalized()
            # без подсказки — ширина горизонтально (плеть лежит на земле)
            local = hint if hint is not None else tangent.cross(UP)
            side = local - tangent * local.dot(tangent)
            if side.length < 1e-5:
                side = tangent.orthogonal()
            side.normalize()
            face = tangent.cross(side).normalized()
            if face.z < 0:
                face = -face
            t = t_range[0] + (t_range[1] - t_range[0]) * i / (n_pts - 1)
            h = widths[i] * 0.5
            rows.append((mb.vert(p - side * h), mb.vert(p + side * h), t, alphas[i], mix_up(face, normal_up)))
        for (a0, a1, t0, c0, n0), (b0, b1, t1, c1, n1) in zip(rows[:-1], rows[1:]):
            mb.face([a0, a1, b1, b0], 0, [suv(strip, t0, -1, span), suv(strip, t0, 1, span), suv(strip, t1, 1, span),
                                          suv(strip, t1, -1, span)], None, [wcol(c0), wcol(c0), wcol(c1), wcol(c1)])
            normals.extend([n0, n0, n1, n1])


def prism(mb, normals, points, radii, strip, alphas, sides=3, normal_up=0.45, span=7.0):
    """Стебель-призма вдоль ломаной; UV поперёк — вся ширина полосы (продольные штрихи видны на каждой грани)."""
    n_pts = len(points)
    ref = Vector((1, 0, 0))
    rings = []
    for i, p in enumerate(points):
        tangent = (points[min(i + 1, n_pts - 1)] - points[max(i - 1, 0)]).normalized()
        nx = ref - tangent * ref.dot(tangent)
        if nx.length < 1e-4:
            nx = tangent.orthogonal()
        nx.normalize()
        ny = tangent.cross(nx)
        ring = []
        for s in range(sides + 1):
            a = s / sides * math.tau
            radial = nx * math.cos(a) + ny * math.sin(a)
            ring.append((p + radial * radii[i], radial))
        rings.append((ring, i / (n_pts - 1), alphas[i]))
    for (r0, t0, c0), (r1, t1, c1) in zip(rings[:-1], rings[1:]):
        for s in range(sides):
            u0, u1 = -1 + 2 * s / sides, -1 + 2 * (s + 1) / sides
            ns = [mix_up(r[s + d][1], normal_up) for r, d in ((r0, 0), (r0, 1), (r1, 1), (r1, 0))]
            quad(mb, normals, (r0[s][0], r0[s + 1][0], r1[s + 1][0], r1[s][0]),
                 (suv(strip, t0, u0, span), suv(strip, t0, u1, span), suv(strip, t1, u1, span), suv(strip, t1, u0, span)),
                 (c0, c0, c1, c1), ns)


def leaf_frame(direction, roll=0.0):
    d = direction.normalized()
    side = d.cross(UP)
    if side.length < 1e-4:
        side = Vector((1, 0, 0))
    side.normalize()
    nrm = side.cross(d).normalized()
    if nrm.z < 0:
        nrm, side = -nrm, -side
    if roll:
        q = Quaternion(d, roll)
        side.rotate(q)
        nrm.rotate(q)
    return d, side, nrm


def poly_leaf(mb, normals, base, direction, length, outline, tris, strip, alpha0, alpha1, droop=0.0, roll=0.0,
              normal_up=0.45, extra_normal=None, extra_weight=0.0, width_scale=1.0, span=2.0):
    """Лист-полигон: outline — вершины (x вдоль 0..1, y поперёк, z из плоскости) в долях длины, tris — треугольники.
    extra_normal — направление (или функция точки), к которому тянутся нормали (купол куста)."""
    d, side, nrm = leaf_frame(direction, roll)
    ymax = max(abs(o[1]) for o in outline) or 1.0
    idx, uvs, cols, nrms = [], [], [], []
    for x, y0, z in outline:
        y = y0 * width_scale
        p = base + d * (x * length) + side * (y * length) + nrm * (z * length) - UP * (droop * length * max(x, 0.0) ** 2)
        idx.append(mb.vert(p))
        uvs.append(suv(strip, x, y0 / ymax, span))
        cols.append(wcol(alpha0 + (alpha1 - alpha0) * max(0.0, min(1.0, x))))
        n = (nrm + side * (y0 / ymax) * 0.35).normalized()
        if extra_normal is not None:
            e = extra_normal(p) if callable(extra_normal) else extra_normal
            n = (n * (1 - extra_weight) + e * extra_weight).normalized()
        nrms.append(mix_up(n, normal_up))
    for a, b, c in tris:
        mb.face([idx[a], idx[b], idx[c]], 0, [uvs[a], uvs[b], uvs[c]], None, [cols[a], cols[b], cols[c]])
        normals.extend([nrms[a], nrms[b], nrms[c]])


def sliver(mb, normals, p0, p1, width, strip, a0, a1, t0=0.0, t1=1.0, normal_up=0.6, side_hint=None):
    """Тонкий треугольник от p0 (ширина width) к острию p1: черешок, щетинка, колосок, веточка."""
    axis = (p1 - p0).normalized()
    hint = side_hint if side_hint is not None else axis.cross(UP)
    side = hint - axis * hint.dot(axis)
    if side.length < 1e-5:
        side = axis.orthogonal()
    side = side.normalized() * width * 0.5
    face = axis.cross(side).normalized()
    if face.z < 0:
        face = -face
    n = mix_up(face, normal_up)
    mb.face([mb.vert(p0 - side), mb.vert(p0 + side), mb.vert(p1)], 0,
            [suv(strip, t0, -1), suv(strip, t0, 1), suv(strip, t1)], None, [wcol(a0), wcol(a0), wcol(a1)])
    normals.extend([n, n, n])


def from_angles(tilt, azimuth):
    return Vector((math.sin(tilt) * math.cos(azimuth), math.sin(tilt) * math.sin(azimuth), math.cos(tilt)))


def pick(rng, weighted):
    r = rng.random() * sum(w for w, _ in weighted)
    for w, v in weighted:
        if r < w:
            return v
        r -= w
    return weighted[-1][1]


def curve_points(base, direction, length, bend, n):
    """Ломаная: от base по direction, к концу заворачивает вверх на bend (0..1)."""
    pts = [base.copy()]
    d = direction.normalized()
    p = base.copy()
    for i in range(n):
        t = (i + 0.5) / n
        step = (d * (1 - bend * t) + UP * bend * t).normalized()
        p = p + step * (length / n)
        pts.append(p.copy())
    return pts


def point_on(pts, t):
    f = max(0.0, min(1.0, t)) * (len(pts) - 1)
    i0 = min(int(f), len(pts) - 2)
    return pts[i0].lerp(pts[i0 + 1], f - i0), (pts[i0 + 1] - pts[i0]).normalized()


def seedling(mb, normals, rng, root, strips, length=(0.05, 0.12), width=(0.004, 0.006), lean=(0.15, 0.5), curl=(0.8, 1.4),
             leaves=None, alpha_max=0.3):
    """Всход злака: 1–3 листа в два ряда (через 180°). Первый — прямой треугольник (колеоптиль с первым листом), следующие
    длиннее и дугой (2 звена)."""
    count = leaves or pick(rng, [(0.3, 1), (0.5, 2), (0.2, 3)])
    yaw = rng.uniform(0, math.tau)
    strip = pick(rng, strips)
    lo, hi = length
    for k in range(count):
        p = (root[0] + rng.uniform(-0.002, 0.002), root[1] + rng.uniform(-0.002, 0.002), root[2])
        if k == 0:
            blade(mb, normals, p, rng.uniform(lo * 0.8, lo + (hi - lo) * 0.35), rng.uniform(*width), rng.uniform(*lean) * 0.5,
                  0.0, yaw + rng.uniform(-0.3, 0.3), strip, segments=1, alpha_max=alpha_max, alpha_h=0.12)
        else:
            blade(mb, normals, p, rng.uniform(lo + (hi - lo) * 0.3, hi), rng.uniform(*width), rng.uniform(*lean),
                  rng.uniform(*curl), yaw + k * math.pi + rng.uniform(-0.35, 0.35), strip, segments=2, alpha_max=alpha_max,
                  alpha_h=0.12, width_power=1.6)


def finish(mb, normals, name):
    obj = mb.to_object(name, [eg.emission_material(name + '_m', lambda nt: nt.nodes.new('ShaderNodeRGB').outputs[0])])
    eg.set_custom_normals(obj, normals)
    return obj


# ------------------------------------------------------------------ N1/N2: стерня

FRESH_01 = dict(radius=1.25, lobes=2, feather=0.25, stalks=250, spacing=0.03, height=(0.10, 0.20, 0.15), width=(0.0035, 0.0045),
                tilt=12.0, broken=0.06, broken_h=(0.04, 0.08), lean=0.0, lodged=0.04, lodged_len=(0.20, 0.30),
                stalk_strips=[(0.5, 'stub'), (0.3, 'stub_b'), (0.2, 'stub_shade')], broken_strip='stub_broken',
                lodged_strip='stub_lodged', mats=118, mat_len=(0.09, 0.20), mat_w=(0.012, 0.026), mat_z=(0.002, 0.012),
                mat_strips=[(0.6, 'mulch_a'), (0.4, 'mulch_b')], chaff=70, chaff_len=(0.05, 0.15), chaff_w=(0.004, 0.008),
                chaff_z=(0.003, 0.02), chaff_strips=[(0.4, 'chaff_a'), (0.35, 'chaff_b'), (0.25, 'chaff_c')], husks=60,
                husk_strip='husk', volunteers=0)
FRESH_02 = dict(FRESH_01, stalks=228, height=(0.08, 0.16, 0.12), lodged=0.10, mats=128, chaff=80, husks=70)
AGED_01 = dict(FRESH_01, stalks=180, height=(0.08, 0.16, 0.12), tilt=16.0, broken=0.075, broken_h=(0.03, 0.07), lean=0.075,
               lodged=0.03, stalk_strips=[(0.5, 'aged'), (0.3, 'aged_b'), (0.2, 'aged_shade')], broken_strip='aged_broken',
               lodged_strip='aged_b', mats=108, mat_z=(0.001, 0.006), mat_strips=[(1.0, 'aged_mulch')], chaff=40,
               chaff_z=(0.001, 0.008), chaff_strips=[(0.35, 'aged_chaff_a'), (0.4, 'aged_chaff_b'), (0.25, 'aged_chaff_c')],
               husks=30, husk_strip='aged_chaff_a', volunteers=25)


def build_stubble(name, seed, cfg):
    rng = random.Random(seed)
    mb = eg.MeshBuilder()
    normals = []
    blob, extent = make_flat_blob(rng, cfg['radius'], cfg['lobes'])
    feather = cfg['feather']
    # кусты по 1–3 стебля (кущение), всего cfg['stalks']
    groups = []
    while sum(groups) < cfg['stalks']:
        groups.append(min(pick(rng, [(0.7, 1), (0.25, 2), (0.05, 3)]), cfg['stalks'] - sum(groups)))
    plants = scatter(rng, len(groups), blob, extent, feather, cfg['spacing'], halo=(0.15, 1.45))
    lo, hi, mode = cfg['height']
    stalks = 0
    for (x, y, inner), count in zip(plants, groups):
        splay = rng.uniform(0, math.tau)
        for j in range(count):
            base = Vector((x, y, 0.0))
            if j:
                base += Vector((rng.uniform(-0.007, 0.007), rng.uniform(-0.007, 0.007), 0))
            az = splay + j * 2.1 + rng.uniform(-0.5, 0.5) if count > 1 else rng.uniform(0, math.tau)
            width = rng.uniform(*cfg['width'])
            r = rng.random()
            if r < cfg['lodged']:
                length = rng.uniform(*cfg['lodged_len'])
                tilt = math.radians(rng.uniform(76, 88))
                top = base + Vector((0, 0, 0.004)) + from_angles(tilt, rng.uniform(0, math.tau)) * length
                stalk(mb, normals, rng, base + Vector((0, 0, 0.004)), top, width, cfg['lodged_strip'], 0.03, normal_up=0.85)
            elif r < cfg['lodged'] + cfg['broken']:
                h = rng.uniform(*cfg['broken_h'])
                top = base + from_angles(math.radians(rng.uniform(0, 25)), az) * h
                stalk(mb, normals, rng, base, top, width, cfg['broken_strip'], 0.15 * h / 0.2)
            else:
                h = rng.triangular(lo, hi, mode)
                if r < cfg['lodged'] + cfg['broken'] + cfg['lean']:
                    tilt = math.radians(rng.uniform(20, 40))
                else:
                    tilt = math.radians(cfg['tilt'] * rng.random() ** 1.5)
                top = base + from_angles(tilt, az) * h
                stalk(mb, normals, rng, base, top, width, pick(rng, cfg['stalk_strips']), 0.15 * min(1.0, h / 0.2))
            stalks += 1
    # пласты измельчённой соломы — основной слой на земле (сверху стебли видны торцом и пропадают)
    for x, y, inner in scatter(rng, cfg['mats'], blob, extent, feather, 0.0, halo=(0.4, 1.8)):
        lo_m, hi_m = cfg['mat_len']
        mat_piece(mb, normals, rng, Vector((x, y, 0)), lo_m + (hi_m - lo_m) * rng.random() ** 0.8, rng.uniform(*cfg['mat_w']),
                  rng.uniform(*cfg['mat_z']), pick(rng, cfg['mat_strips']))
    # отдельные соломины
    for x, y, inner in scatter(rng, cfg['chaff'], blob, extent, feather, 0.0, halo=(0.4, 1.8)):
        lo_c, hi_c = cfg['chaff_len']
        length = lo_c + (hi_c - lo_c) * rng.random() ** 0.6
        flat_piece(mb, normals, Vector((x, y, 0)), rng.uniform(0, math.tau), length, rng.uniform(*cfg['chaff_w']),
                   rng.uniform(*cfg['chaff_z']), rng.uniform(0, 0.012) * length / 0.15, pick(rng, cfg['chaff_strips']))
    # мелкая полова 1–2 см
    for x, y, inner in scatter(rng, cfg['husks'], blob, extent, feather, 0.0, halo=(0.4, 1.8)):
        husk(mb, normals, rng, Vector((x, y, 0)), rng.uniform(0.008, 0.016), rng.uniform(0.001, 0.008), cfg['husk_strip'])
    # падалица: всходы осыпавшегося зерна в 2–3 полосах (валки половы)
    if cfg['volunteers']:
        heading = rng.uniform(0, math.pi)
        along, across = Vector((math.cos(heading), math.sin(heading), 0)), Vector((-math.sin(heading), math.cos(heading), 0))
        lines = [rng.uniform(-0.4, 0.4) for _ in range(rng.randint(2, 3))]
        placed = tries = 0
        while placed < cfg['volunteers'] and tries < 5000:
            tries += 1
            p = along * rng.uniform(-extent, extent) + across * (rng.choice(lines) + rng.gauss(0, 0.03))
            if blob(p.x, p.y) < 0.12:
                continue
            placed += 1
            seedling(mb, normals, rng, (p.x, p.y, 0.0), [(0.5, 'volunteer'), (0.5, 'volunteer_b')], length=(0.08, 0.15),
                     width=(0.0035, 0.0045), lean=(0.25, 0.7), curl=(0.5, 1.1), alpha_max=0.35)
    return finish(mb, normals, name), mb.triangle_count, dict(stalks=stalks)


# ------------------------------------------------------------------ N3: всходы озимой пшеницы

SPROUTS = dict(radius=1.25, lobes=2, feather=0.25, budget=1100, spacing=0.012, clump_cell=0.22, clump=0.5, pairs=0.15,
               halo=(0.25, 1.6))


def build_sprouts(name, seed, cfg):
    """Россыпь всходов по пятну ~2.5 м (ровно внутри, мягкий край), кучность — шум ~22 см (0.5–1), 15 % — парами (два
    зерна рядом); без рядов. Растений столько, сколько входит в бюджет треугольников."""
    rng = random.Random(seed)
    mb = eg.MeshBuilder()
    normals = []
    blob, extent = make_flat_blob(rng, cfg['radius'], cfg['lobes'])
    noise = value_noise(rng, cfg['clump_cell'])
    density = lambda x, y: 1.0 - cfg['clump'] + cfg['clump'] * noise(x, y)  # noqa: E731
    strips = [(0.45, 'sprout'), (0.3, 'sprout_b'), (0.25, 'sprout_c')]
    plants = 0
    grid = Grid(cfg['spacing'])
    tries = 0
    while mb.triangle_count < cfg['budget'] - 7 and tries < 200000:
        tries += 1
        if rng.random() < cfg['halo'][0]:
            sc = cfg['halo'][1]
            x, y = rng.uniform(-extent * sc, extent * sc), rng.uniform(-extent * sc, extent * sc)
            inner = smooth(-0.05, 0.5, blob(x / sc, y / sc))
        else:
            x, y = rng.uniform(-extent, extent), rng.uniform(-extent, extent)
            inner = smooth(-0.05, cfg['feather'], blob(x, y))
        if inner <= 0 or rng.random() > inner * density(x, y) or grid.near(x, y, cfg['spacing']):
            continue
        grid.add(x, y)
        seedling(mb, normals, rng, (x, y, 0.0), strips)
        plants += 1
        if rng.random() < cfg['pairs'] and mb.triangle_count < cfg['budget'] - 7:
            a = rng.uniform(0, math.tau)
            seedling(mb, normals, rng, (x + math.cos(a) * 0.015, y + math.sin(a) * 0.015, 0.0), strips)
            plants += 1
    return finish(mb, normals, name), mb.triangle_count, dict(plants=plants)


# ------------------------------------------------------------------ N4: вьюнок полевой

# стреловидный лист: выемка у черешка (0), ушки назад, «талия», плечо, острая верхушка; веер из выемки — 6 треугольников
ARROW = [(0.0, 0.0, 0.0), (-0.25, 0.30, 0.03), (0.10, 0.15, 0.02), (0.46, 0.23, 0.05), (1.0, 0.0, 0.0), (0.46, -0.23, 0.05),
         (0.10, -0.15, 0.02), (-0.25, -0.30, 0.03)]
ARROW_TRIS = [(0, 1, 2), (0, 2, 3), (0, 3, 4), (0, 4, 5), (0, 5, 6), (0, 6, 7)]


def runner_path(rng, start, heading, length, segs, meander=0.35):
    """Плеть по земле: извивается, местами приподнята (на стерне, комке), не выше 3 см."""
    p = start.copy()
    pts = [p.copy()]
    for s in range(segs):
        heading += rng.gauss(0, meander)
        p = p + Vector((math.cos(heading), math.sin(heading), 0)) * (length / segs)
        p.z = 0.004 + 0.025 * rng.random() ** 3
        pts.append(p.copy())
    return pts


def funnel(mb, normals, rng, throat_base, axis, radius, depth, alpha, strip='bw_flower'):
    """Цветок вьюнка: воронка с пятью неглубокими лопастями (10 вершин края: кончики лопастей и выемки), жёлтый зев
    в глубине, розовая полоса от зева к кончику каждой лопасти; 10 треугольников."""
    sx = axis.orthogonal().normalized()
    sy = axis.cross(sx)
    centre = mb.vert(throat_base)
    rim_centre = throat_base + axis * depth
    phase = rng.uniform(0, math.tau)
    rim = []
    for j in range(10):
        a = phase + j / 10 * math.tau
        tip = j % 2 == 0
        r = radius * (1.0 if tip else 0.86)
        p = rim_centre + (sx * math.cos(a) + sy * math.sin(a)) * r + axis * (0.0012 if tip else 0.0)
        rim.append((mb.vert(p), tip, (p - throat_base).normalized()))
    for j in range(10):
        (v0, tip0, d0), (v1, tip1, d1) = rim[j], rim[(j + 1) % 10]
        uv0 = suv(strip, 1.0, 0.0, 7) if tip0 else suv(strip, 0.95, 1.0, 7)
        uv1 = suv(strip, 1.0, 0.0, 7) if tip1 else suv(strip, 0.95, 1.0, 7)
        mb.face([centre, v0, v1], 0, [suv(strip, 0.0, 0.0, 7), uv0, uv1], None, [wcol(alpha)] * 3)
        normals.extend([mix_up(axis, 0.3), mix_up((axis + d0).normalized(), 0.3), mix_up((axis + d1).normalized(), 0.3)])


def build_bindweed(name, seed, cfg=None):
    """Мат вьюнка: 7–9 плетей из корневой шейки (начало координат) во все стороны, треть ветвится; листья очерёдно на
    черешках, у основания и на концах плетей мельче (на концах — молодые светлые), посередине крупнее; цветки на
    цветоножках над плетями, смотрят вверх."""
    rng = random.Random(seed)
    mb = eg.MeshBuilder()
    normals = []
    runners = rng.randint(7, 9)
    paths = []
    leaves = 0

    def leaves_along(pts, count, size_range, t_range=(0.1, 0.97)):
        nonlocal leaves
        for k in range(count):
            t = t_range[0] + (t_range[1] - t_range[0]) * (k + rng.uniform(-0.3, 0.3)) / max(1, count - 1)
            at, tangent = point_on(pts, t)
            side = tangent.cross(UP).normalized() * (1 if k % 2 else -1)
            size = rng.uniform(*size_range) * (0.55 + 0.6 * math.sin(math.pi * min(1.0, t * 1.08)))
            if rng.random() < 0.15:
                size *= 1.25
            a = rng.uniform(0.4, 1.3)
            leaf_dir = (tangent * math.cos(a) + side * math.sin(a) + UP * rng.uniform(0.05, 0.35)).normalized()
            leaf_base = at + (side * 0.7 + tangent * 0.3 + UP * 0.5).normalized() * size * rng.uniform(0.3, 0.5)
            sliver(mb, normals, at, leaf_base, 0.0016, 'bw_runner', 0.08, 0.12)
            strip = 'bw_leaf_young' if t > 0.85 else ('bw_leaf_lit' if rng.random() < 0.35 else 'bw_leaf')
            poly_leaf(mb, normals, leaf_base, leaf_dir, size, ARROW, ARROW_TRIS, strip, 0.1, 0.16,
                      droop=rng.uniform(0.0, 0.15), roll=rng.uniform(-0.3, 0.3), normal_up=0.5, width_scale=rng.uniform(0.85, 1.2))
            leaves += 1

    golden = math.radians(137.5)
    h0 = rng.uniform(0, math.tau)
    for i in range(runners):
        heading = h0 + i * golden + rng.uniform(-0.2, 0.2)
        segs = 5
        length = rng.uniform(0.22, 0.40)
        pts = runner_path(rng, Vector((rng.uniform(-0.006, 0.006), rng.uniform(-0.006, 0.006), 0.004)), heading, length, segs)
        paths.append(pts)
        ribbon(mb, normals, pts, [0.0024] + [0.002] * segs, 'bw_runner', [0.03 + 0.08 * k / segs for k in range(segs + 1)], None,
               normal_up=0.7)
        leaves_along(pts, 7 + (1 if length > 0.3 else 0), (0.026, 0.044))
        # плети ветвятся: короткий побег из середины
        if rng.random() < 0.4:
            at, tangent = point_on(pts, rng.uniform(0.35, 0.6))
            side_heading = math.atan2(tangent.y, tangent.x) + rng.choice((-1, 1)) * rng.uniform(0.6, 1.1)
            branch = runner_path(rng, at, side_heading, rng.uniform(0.09, 0.15), 2)
            ribbon(mb, normals, branch, [0.0018] * len(branch), 'bw_runner', [0.08] * len(branch), None, normal_up=0.7)
            leaves_along(branch, 3, (0.02, 0.032), (0.3, 0.95))
    # воронки цветков на цветоножках, смотрят вверх (днём раскрыты)
    flowers = rng.randint(6, 9)
    for k in range(flowers):
        pts = paths[k % len(paths)]
        at, _ = point_on(pts, rng.uniform(0.3, 0.85))
        axis = from_angles(rng.uniform(0, 0.4), rng.uniform(0, math.tau))
        throat = at + Vector((rng.uniform(-0.01, 0.01), rng.uniform(-0.01, 0.01), rng.uniform(0.02, 0.04)))
        sliver(mb, normals, at, throat, 0.0016, 'bw_runner', 0.1, 0.15, side_hint=Vector((1, 0, 0)))
        funnel(mb, normals, rng, throat, axis, rng.uniform(0.010, 0.0125), rng.uniform(0.008, 0.010), 0.15,
               'bw_flower_pink' if rng.random() < 0.4 else 'bw_flower')
    return finish(mb, normals, name), mb.triangle_count, dict(runners=runners, leaves=leaves, flowers=flowers)


# ------------------------------------------------------------------ N5: марь белая

# ромбический лист: клиновидное основание, угловатые зубцы-лопасти по бокам, острая верхушка; веер из основания — 4 треугольника
RHOMB = [(0.0, 0.0, 0.0), (0.36, 0.42, 0.04), (0.64, 0.27, 0.02), (1.0, 0.0, -0.01), (0.64, -0.27, 0.02), (0.36, -0.42, 0.04)]
RHOMB_TRIS = [(0, 1, 2), (0, 2, 3), (0, 3, 4), (0, 4, 5)]
# верхний лист — узкий цельный
LANCE = [(0.0, 0.0, 0.0), (0.38, 0.18, 0.05), (1.0, 0.0, 0.0), (0.38, -0.18, 0.05)]
LANCE_TRIS = [(0, 1, 2), (0, 2, 3)]


def lump(mb, normals, centre, size, strip, alpha, rng, up=None):
    """Клубочек соцветия: два скрещённых ромба (4 треугольника) — бугорок с любой стороны; нормали — от центра и вверх."""
    up = (up or UP).normalized()
    a = rng.uniform(0, math.pi)
    for k in range(2):
        h = Vector((math.cos(a + k * math.pi / 2), math.sin(a + k * math.pi / 2), 0))
        h = (h - up * h.dot(up)).normalized()
        top, bottom = centre + up * size * 0.55, centre - up * size * 0.45
        left, right = centre - h * size * 0.38, centre + h * size * 0.38
        pts = (bottom, right, top, left)
        uvs = (suv(strip, 0.1, 0, 7), suv(strip, 0.5, 1, 7), suv(strip, 0.9, 0, 7), suv(strip, 0.5, -1, 7))
        ns = [mix_up(((p - centre).normalized() * 0.6 + up * 0.4).normalized(), 0.55) for p in pts]
        quad(mb, normals, pts, uvs, (alpha,) * 4, ns)


def build_goosefoot(name, seed, cfg=None):
    """Куст мари: главный стебель ~0.5 м, 5–6 ветвей из нижних двух третей (нижние длиннее, расходятся на 40–60° и
    загибаются вверх) — ширина ~0.3 м; листья по спирали, внизу ромбические на черешках, вверху узкие; клубочки соцветий —
    метёлка на верхушке, колоски на концах ветвей и в пазухах верхних листьев."""
    rng = random.Random(seed)
    mb = eg.MeshBuilder()
    normals = []
    H = 0.50
    lean = from_angles(rng.uniform(0.0, 0.06), rng.uniform(0, math.tau))
    stem_pts = [Vector((0, 0, 0))]
    for i in range(1, 4):
        t = i / 3
        stem_pts.append(lean * (H * t) + Vector((rng.gauss(0, 0.006), rng.gauss(0, 0.006), 0)) * t)
    alpha = lambda z: 0.6 * min(1.0, max(0.0, z / 0.6)) ** 1.5  # noqa: E731
    prism(mb, normals, stem_pts, [0.0034, 0.0029, 0.0022, 0.0013], 'gf_stem', [alpha(p.z) for p in stem_pts], sides=3)
    leaf_count = 0
    lumps = 0
    mealy = [(0.65, 'gf_mealy'), (0.35, 'gf_mealy_g')]

    def leaf(base, az, elev, size, t, petiole_share):
        nonlocal leaf_count
        d = Vector((math.cos(elev) * math.cos(az), math.cos(elev) * math.sin(az), math.sin(elev)))
        if petiole_share > 0:
            tip = base + from_angles(math.radians(rng.uniform(35, 55)), az) * size * petiole_share
            sliver(mb, normals, base, tip, 0.002, 'gf_stem', alpha(base.z), alpha(base.z) + 0.05, 0.3, 0.6)
            base = tip
        shape = (RHOMB, RHOMB_TRIS) if t < 0.7 else (LANCE, LANCE_TRIS)
        strip = 'gf_leaf_young' if t > 0.82 or (t > 0.62 and rng.random() < 0.35) else 'gf_leaf'
        poly_leaf(mb, normals, base, d, size, *shape, strip, alpha(base.z) + 0.05, alpha(base.z) + 0.2,
                  droop=rng.uniform(0.15, 0.4) * (1.2 - t), roll=rng.uniform(-0.3, 0.3), width_scale=rng.uniform(0.9, 1.15))
        leaf_count += 1

    def spike(start, axis, count, size, spread):
        """Колосок из клубочков вдоль оси, с разбросом в стороны (бугристый, неровный)."""
        nonlocal lumps
        for k in range(count):
            p = start + axis * (size * 0.55 * k) + Vector((rng.gauss(0, spread), rng.gauss(0, spread), rng.gauss(0, spread * 0.5)))
            lump(mb, normals, p, size * rng.uniform(0.8, 1.15), pick(rng, mealy), alpha(p.z) + 0.1, rng, axis)
            lumps += 1

    # ветви из нижних двух третей, по спирали
    branches = 6
    golden = math.radians(137.5)
    az0 = rng.uniform(0, math.tau)
    for b in range(branches):
        t = 0.14 + 0.48 * (b + rng.uniform(0, 0.6)) / branches
        at, _ = point_on(stem_pts, t)
        az = az0 + b * golden + rng.uniform(-0.25, 0.25)
        length = (0.25 - 0.12 * (t - 0.14) / 0.48) * rng.uniform(0.9, 1.1)
        tilt = math.radians(eg.lerp(rng.uniform(58, 68), rng.uniform(38, 48), (t - 0.14) / 0.48))
        pts = curve_points(at, from_angles(tilt, az), length, 0.35, 2)
        ribbon(mb, normals, pts, [0.0042, 0.003, 0.002], 'gf_stem', [alpha(p.z) for p in pts],
               Vector((-math.sin(az), math.cos(az), 0)), normal_up=0.5, crossed=True, span=7.0)
        for k in range(6):
            lt = 0.18 + 0.74 * k / 5 + rng.uniform(-0.04, 0.04)
            p, tangent = point_on(pts, lt)
            leaf_az = math.atan2(tangent.y, tangent.x) + (1 if k % 2 else -1) * rng.uniform(0.6, 1.3)
            leaf(p, leaf_az, math.radians(rng.uniform(10, 35)), rng.uniform(0.05, 0.065) * (1.1 - 0.4 * lt), 0.35 + 0.4 * lt, 0.0)
        tip_axis = ((pts[-1] - pts[-2]).normalized() + UP * 0.6).normalized()
        spike(pts[-1], tip_axis, 3, rng.uniform(0.016, 0.02), 0.004)
    # листья главного стебля по спирали, к верху мельче и гуще; нижние — ромбические на длинных черешках
    main = 23
    for k in range(main):
        u = (k + rng.uniform(0.2, 0.8)) / main
        t = 0.06 + 0.9 * (1 - (1 - u) ** 1.35)
        at, tangent = point_on(stem_pts, t)
        az = az0 + 0.5 + k * golden + rng.uniform(-0.2, 0.2)
        size = eg.lerp(0.085, 0.035, t ** 0.8) * rng.uniform(0.9, 1.05)
        base = at + Vector((math.cos(az), math.sin(az), 0)) * 0.004
        leaf(base, az, math.radians(eg.lerp(rng.uniform(0, 20), rng.uniform(30, 55), t)), size, t,
             rng.uniform(0.35, 0.5) if t < 0.45 else 0.0)
        # клубочки в пазухах верхних листьев
        if t > 0.6 and rng.random() < 0.3:
            lump(mb, normals, base + Vector((math.cos(az), math.sin(az), 0)) * 0.01 + Vector((0, 0, 0.008)),
                 rng.uniform(0.012, 0.015), pick(rng, mealy), alpha(base.z) + 0.1, rng)
            lumps += 1
    # верхушечная метёлка: ось 9–10 см из клубочков и 2 боковых колоска
    top, tangent = stem_pts[-1], (stem_pts[-1] - stem_pts[-2]).normalized()
    spike(top, tangent, 5, rng.uniform(0.018, 0.021), 0.005)
    for k in range(2):
        at, _ = point_on(stem_pts, 0.9 + 0.05 * k)
        side_axis = from_angles(math.radians(rng.uniform(25, 40)), az0 + k * math.pi + rng.uniform(-0.5, 0.5))
        spike(at + side_axis * 0.012, side_axis, 2, rng.uniform(0.016, 0.019), 0.004)
    return finish(mb, normals, name), mb.triangle_count, dict(leaves=leaf_count, branches=branches, lumps=lumps)


# ------------------------------------------------------------------ N6: щетинник

def fuzzy_spike(mb, normals, pts, half_widths, strip, alpha, planes=2, roll0=0.0):
    """Колосовидная метёлка: planes скрещённых полосок вдоль изогнутой оси; на каждом звене с обеих сторон — треугольник
    щетинок за край (пушистый неровный силуэт, а не гладкий цилиндр); острый кончик."""
    n_pts = len(pts)
    for k in range(planes):
        roll = roll0 + k * math.pi / planes
        sides = []
        for i, p in enumerate(pts):
            tangent = (pts[min(i + 1, n_pts - 1)] - pts[max(i - 1, 0)]).normalized()
            s = tangent.cross(UP)
            if s.length < 1e-4:
                s = tangent.orthogonal()
            s.normalize()
            s.rotate(Quaternion(tangent, roll))
            sides.append((s, tangent))
        for i in range(n_pts - 1):
            (s0, t0), (s1, t1) = sides[i], sides[i + 1]
            p0, p1 = pts[i], pts[i + 1]
            w0, w1 = half_widths[i], half_widths[i + 1]
            face = t0.cross(s0).normalized()
            if face.z < 0:
                face = -face
            n = mix_up(face, 0.72)
            v0, v1 = i / (n_pts - 1), (i + 1) / (n_pts - 1)
            if i == n_pts - 2 and w1 <= 1e-5:
                tri(mb, normals, (p0 - s0 * w0, p0 + s0 * w0, p1), (suv(strip, v0, -1), suv(strip, v0, 1), suv(strip, v1)),
                    (alpha,) * 3, (n, n, n))
                continue
            quad(mb, normals, (p0 - s0 * w0, p0 + s0 * w0, p1 + s1 * w1, p1 - s1 * w1),
                 (suv(strip, v0, -1), suv(strip, v0, 1), suv(strip, v1, 1), suv(strip, v1, -1)), (alpha,) * 4, (n,) * 4)
            mid = p0.lerp(p1, 0.55)
            wm = (w0 + w1) * 0.5
            for sgn in (-1, 1):
                tip = mid + s0 * sgn * wm * 1.9 + t0 * wm * 0.6
                tri(mb, normals, (p0 + s0 * sgn * w0 * 0.9, p1 + s1 * sgn * w1 * 0.9, tip),
                    (suv(strip, v0, sgn * 0.8), suv(strip, v1, sgn * 0.8), suv(strip, (v0 + v1) * 0.5 + 0.1, sgn)), (alpha,) * 3,
                    (n,) * 3)


def build_foxtail(name, seed, cfg=None):
    rng = random.Random(seed)
    mb = eg.MeshBuilder()
    normals = []
    for k in range(rng.randint(10, 13)):
        root = (rng.uniform(-0.012, 0.012), rng.uniform(-0.012, 0.012), 0.0)
        length = rng.uniform(0.08, 0.20)
        blade(mb, normals, root, length, rng.uniform(0.004, 0.007), rng.uniform(0.45, 1.1),
              rng.uniform(0.3, 0.8), rng.uniform(0, math.tau), pick(rng, [(0.6, 'fox_blade'), (0.4, 'fox_blade_b')]),
              segments=3 if length > 0.13 else 2, alpha_max=0.5, alpha_h=0.2, width_power=1.4,
              twist=math.radians(rng.uniform(0, 40)))
    culms = rng.randint(4, 5)
    for c in range(culms):
        az = c / culms * math.tau + rng.uniform(-0.4, 0.4)
        height = rng.uniform(0.20, 0.35)
        base = Vector((rng.uniform(-0.01, 0.01), rng.uniform(-0.01, 0.01), 0.0))
        # коленчатая соломина: короткое звено вбок, затем вверх
        knee = base + from_angles(rng.uniform(0.35, 0.7), az) * 0.04
        rise = from_angles(rng.uniform(0.02, 0.2), az)
        mid = knee + rise * (height - 0.04) * 0.55
        top = knee + (rise + from_angles(rng.uniform(0.0, 0.15), az + rng.uniform(-1, 1)) * 0.3).normalized() * (height - 0.04)
        ribbon(mb, normals, [base, knee, mid, top], [0.0026, 0.0024, 0.002, 0.0016], 'fox_culm', [0.0, 0.05, 0.35, 0.75],
               Vector((-math.sin(az), math.cos(az), 0)), normal_up=0.72, crossed=True)
        # два листа на соломине (влагалище обнимает стебель, пластинка отходит дугой)
        for j in range(2):
            leaf_at = knee.lerp(mid, rng.uniform(0.15, 0.45) + 0.4 * j)
            blade(mb, normals, tuple(leaf_at), rng.uniform(0.06, 0.12) * (1 - 0.3 * j), 0.005, rng.uniform(0.6, 0.95),
                  rng.uniform(0.3, 0.6), az + rng.uniform(-1.2, 1.2) + j * math.pi, 'fox_blade', segments=2, alpha_max=0.5,
                  alpha_h=0.2)
        # метёлка 3–6 см, ядро ~6 мм, щетинки до ~11 мм; у S. viridis кончик поникает, у жёлтых (S. pumila) прямая
        yellow = rng.random() < 0.3
        strip = 'fox_spike_y' if yellow else 'fox_spike'
        length = rng.uniform(0.04, 0.07)
        nod = rng.uniform(0.0, 0.12) if yellow else rng.uniform(0.25, 0.55)
        d = (top - mid).normalized()
        out = Vector((math.cos(az), math.sin(az), 0))
        pts = [top.copy()]
        for i in range(3):
            d = (d * math.cos(nod / 3 * (i + 1)) + out * math.sin(nod / 3 * (i + 1))).normalized() if nod > 0 else d
            pts.append(pts[-1] + d * length / 3)
        hw = [0.0026, 0.0038, 0.0034, 0.0]
        fuzzy_spike(mb, normals, pts, hw, strip, 0.8, planes=2, roll0=rng.uniform(0, math.pi))
    return finish(mb, normals, name), mb.triangle_count, dict(culms=culms)


# ------------------------------------------------------------------ N7: полынь горькая

# лист полыни — яйцевидный полигон (4 треугольника); рассечённость — в полосе атласа (pinnate: светлые доли и рахис,
# между долями тень): вблизи лист перистый, издали сливается в мягкую серо-зелёную массу. Узкие доли геометрией
# давали колючие звёздочки, лопастные полигоны — бумажные стрелки
OVATE = [(0.0, 0.0, 0.0), (0.28, 0.40, 0.05), (0.68, 0.34, 0.04), (1.0, 0.0, 0.0), (0.68, -0.34, 0.04), (0.28, -0.40, 0.05)]
OVATE_TRIS = [(0, 1, 2), (0, 2, 3), (0, 3, 4), (0, 4, 5)]


def panicle(mb, normals, rng, start, axis, length, width, alpha, dome):
    """Веточка метёлки полыни: ромб вдоль оси (2 треугольника), в полосе — серо-зелёная ось с крапом серо-жёлтых
    корзинок 3–5 мм (speckle): вблизи — россыпь мелких шариков, издали — серо-жёлтая дымка верхушки."""
    side = axis.cross(UP)
    if side.length < 1e-4:
        side = axis.orthogonal()
    side.normalize()
    side.rotate(Quaternion(axis, rng.uniform(-0.9, 0.9)))
    tip = start + axis * length
    mid = start + axis * length * 0.42
    pts = (start, mid + side * width * 0.5, tip, mid - side * width * 0.5)
    face = axis.cross(side).normalized()
    if face.z < 0:
        face = -face
    ns = [mix_up(((face * 0.4 + dome(p) * 0.6).normalized()), 0.4) for p in pts]
    quad(mb, normals, pts, (suv('ww_panicle', 0.0, 0, 7), suv('ww_panicle', 0.42, 1, 7), suv('ww_panicle', 1.0, 0, 7),
                            suv('ww_panicle', 0.42, -1, 7)), (alpha, alpha + 0.05, alpha + 0.1, alpha + 0.05), ns)
    return 1


def build_wormwood(name, seed, cfg=None):
    """Холмик полыни (~0.8 м, ~0.6 м в ширину, шире книзу): у земли розетка крупных листьев на черешках; из
    одревесневшего основания 9 стеблей — средние прямые и высокие, крайние ниже, клонятся наружу и загибаются вверх;
    листья по всей длине стебля (внизу крупные, горизонтальные и поникшие, выше мельче и торчком), верхняя четверть —
    веточки метёлки с серо-жёлтыми корзинками и листочками. Объём — тоном: листья в глубине куста — тёмная полоса
    (тень внутри, теней у деталей нет), снаружи сверху — светлая; нормали — на 60 % от центра холмика (мягкий купол)."""
    rng = random.Random(seed)
    mb = eg.MeshBuilder()
    normals = []
    H = 0.80
    centre = Vector((0, 0, H * 0.32))
    dome = lambda p: Vector((p.x - centre.x, p.y - centre.y, (p.z - centre.z) * 0.8 + 0.12)).normalized()  # noqa: E731
    alpha = lambda z: 0.5 * min(1.0, max(0.0, z / H)) ** 1.4  # noqa: E731
    radius_at = lambda z: 0.06 + 0.24 * math.sin(math.pi * min(1.0, 0.12 + 0.88 * z / H)) ** 0.7  # noqa: E731
    leaves = heads = 0

    def leaf(at, d, size, petiole=0.0, droop=0.2, strip=None):
        nonlocal leaves
        if petiole > 0:
            tip = at + (d + UP * 0.25).normalized() * size * petiole
            sliver(mb, normals, at, tip, 0.003, 'ww_stem', alpha(at.z), alpha(tip.z), 0.2, 0.8, normal_up=0.6)
            at = tip
        if strip is None:
            mid = at + d * size * 0.5
            inner = 1.0 - math.hypot(mid.x, mid.y) / radius_at(mid.z)
            if inner > 0.5:
                strip = 'ww_leaf_in'
            elif inner < 0.25 and d.z > 0.2 and rng.random() < 0.6:
                strip = 'ww_leaf_lit'
            else:
                strip = 'ww_leaf'
        poly_leaf(mb, normals, at, d, size, OVATE, OVATE_TRIS, strip, alpha(at.z), alpha(at.z) + 0.1,
                  droop=droop, roll=rng.uniform(-0.45, 0.45), normal_up=0.35, extra_normal=dome, extra_weight=0.6,
                  width_scale=rng.uniform(0.9, 1.2), span=7.0)
        leaves += 1

    # розетка: крупные листья на черешках почти горизонтально, наружу — нижний край холмика
    for k in range(7):
        az = k / 7 * math.tau + rng.uniform(-0.25, 0.25)
        start = Vector((math.cos(az) * 0.025, math.sin(az) * 0.025, rng.uniform(0.01, 0.03)))
        leaf(start, from_angles(math.radians(rng.uniform(64, 82)), az), rng.uniform(0.13, 0.17), petiole=0.3,
             droop=rng.uniform(0.15, 0.3), strip=pick(rng, [(0.6, 'ww_leaf'), (0.4, 'ww_leaf_lit')]))
    stems = 9
    paths = []
    az0 = rng.uniform(0, math.tau)
    for i in range(stems):
        az = az0 + i / stems * math.tau + rng.uniform(-0.25, 0.25)
        outer = rng.uniform(0.6, 1.0) if i % 2 else rng.uniform(0.0, 0.3)  # через один — крайние
        r = 0.01 + 0.035 * outer
        tilt = eg.lerp(rng.uniform(0.05, 0.15), rng.uniform(0.55, 0.8), outer)
        length = H * (1.0 - 0.25 * outer) * rng.uniform(0.9, 1.0)
        base = Vector((math.cos(az) * r, math.sin(az) * r, 0.0))
        pts = curve_points(base, from_angles(tilt, az), length, 0.2 + 0.45 * outer, 2)
        ribbon(mb, normals, pts, [0.0045, 0.003, 0.0015], 'ww_stem', [alpha(p.z) for p in pts],
               Vector((-math.sin(az), math.cos(az), 0)), normal_up=0.75)
        paths.append((pts, az))
        # листья по всей длине стебля (8–88 %): внизу 10–12 см, горизонтальные и поникшие, наружу от куста; выше — мельче
        # и торчком, в метёлке — 4–6 см
        for k in range(9):
            t = 0.08 + 0.74 * (k + rng.uniform(0.0, 0.7)) / 9
            at, tangent = point_on(pts, t)
            out_az = az + rng.uniform(-1.5, 1.5)
            elev = math.radians(eg.lerp(rng.uniform(-20, 5), rng.uniform(30, 60), t / 0.82))
            d = Vector((math.cos(elev) * math.cos(out_az), math.cos(elev) * math.sin(out_az), math.sin(elev)))
            leaf(at, d, eg.lerp(rng.uniform(0.10, 0.12), rng.uniform(0.045, 0.06), t / 0.82), droop=eg.lerp(0.35, 0.1, t))
        # метёлка в верхней трети: 4 веточки вверх-наружу (ромбы с крапом корзинок) и верхушечная — узкая метёлка
        for k in range(4):
            t = 0.66 + 0.08 * k + rng.uniform(-0.02, 0.02)
            at, tangent = point_on(pts, t)
            twig_az = az + rng.uniform(-2.0, 2.0)
            d = (tangent + from_angles(math.pi / 2, twig_az) * math.tan(math.radians(rng.uniform(25, 45)))).normalized()
            heads += panicle(mb, normals, rng, at, d, rng.uniform(0.07, 0.11) * (1.1 - 0.15 * k), rng.uniform(0.026, 0.034),
                             alpha(at.z), dome)
        top, tangent = pts[-1], (pts[-1] - pts[-2]).normalized()
        heads += panicle(mb, normals, rng, top - tangent * 0.09, (tangent + UP * 0.3).normalized(), 0.11, 0.024, alpha(top.z), dome)
    # добивка купола: листья наружу на случайных стеблях — закрывают просветы между стеблями
    for k in range(26):
        pts, az = rng.choice(paths)
        at, _ = point_on(pts, rng.uniform(0.2, 0.7))
        out = Vector((at.x - centre.x, at.y - centre.y, 0.0))
        out_az = (math.atan2(out.y, out.x) if out.length > 1e-3 else az) + rng.uniform(-0.6, 0.6)
        leaf(at, from_angles(math.radians(rng.uniform(55, 85)), out_az), rng.uniform(0.07, 0.095), droop=0.25)
    # одревесневшее основание: короткие бурые пеньки
    for k in range(3):
        az = rng.uniform(0, math.tau)
        b = Vector((math.cos(az) * 0.015, math.sin(az) * 0.015, 0.0))
        sliver(mb, normals, b, b + from_angles(0.3, az) * 0.07, 0.006, 'ww_wood', 0.0, 0.03, 0.0, 1.0, normal_up=0.6)
    # прошлогодний сухой стебель: прямой, без листьев, с обломком веточки
    az = rng.uniform(0, math.tau)
    base = Vector((math.cos(az) * 0.02, math.sin(az) * 0.02, 0))
    pts = curve_points(base, from_angles(rng.uniform(0.05, 0.2), az), H * rng.uniform(0.9, 1.05), 0.1, 2)
    ribbon(mb, normals, pts, [0.0045, 0.003, 0.0018], 'ww_dry', [alpha(p.z) * 0.6 for p in pts], Vector((-math.sin(az), math.cos(az), 0)),
           normal_up=0.7)
    at, tangent = point_on(pts, 0.8)
    sliver(mb, normals, at, at + (tangent + from_angles(math.pi / 2, az + 1.5)).normalized() * 0.05, 0.002, 'ww_dry',
           alpha(at.z) * 0.6, alpha(at.z) * 0.6, 0.6, 1.0, normal_up=0.7)
    return finish(mb, normals, name), mb.triangle_count, dict(stems=stems, leaves=leaves, panicles=heads)


# ------------------------------------------------------------------ материалы и манифесты

BASE_MATERIAL = dict(role='grass', albedo=ATLAS_NAME + '_albedo.png', normal=ATLAS_NAME + '_normal.png', mask=ATLAS_NAME + '_mask.png',
                     alphaCutoff=0.05, alphaDrag=1.0, initialBend=1.0, normalScale=0.5, healthyColor=[1.0, 1.0, 1.0],
                     rotorWashClear=0.0, invertBackfaceNormal=0.0, aoMax=1.0, smoothnessMin=0.0, distanceControl=1.0)
MATERIALS = {
    # стерня мертва — по сезону не перекрашивается (seasonKeep 2); пятно-ковёр — sizeVariation ≤ 0.1
    'field_stubble_fresh': dict(stiffness=0.9, drag=0.2, shiverDrag=0.01, shiverDirectionality=0.5, specular=0.2, smoothnessMax=0.15,
                                aoMin=0.55, dryColor=[0.96, 0.94, 0.88], colorNoiseSpread=0.3, puddleSink=0.12, sizeVariation=0.08,
                                scaleFade=[100000, 10], seasonKeep=2.0),
    'field_stubble_aged': dict(stiffness=0.9, drag=0.2, shiverDrag=0.01, shiverDirectionality=0.5, specular=0.18, smoothnessMax=0.12,
                               aoMin=0.55, dryColor=[0.94, 0.93, 0.9], colorNoiseSpread=0.3, puddleSink=0.12, sizeVariation=0.08,
                               scaleFade=[100000, 10], seasonKeep=2.0),
    'field_sprouts': dict(stiffness=1.0, drag=0.5, shiverDrag=0.04, shiverDirectionality=0.6, specular=0.25, smoothnessMax=0.3,
                          aoMin=0.6, dryColor=[0.97, 0.97, 0.9], colorNoiseSpread=0.3, puddleSink=0.08, sizeVariation=0.1,
                          scaleFade=[100000, 10], seasonKeep=1.0),
    'field_weed_bindweed': dict(stiffness=1.2, drag=0.3, shiverDrag=0.05, shiverDirectionality=0.5, specular=0.3, smoothnessMax=0.35,
                                aoMin=0.6, dryColor=[0.94, 0.92, 0.8], colorNoiseSpread=0.3, puddleSink=0.05, sizeVariation=0.3,
                                scaleFade=[100000, 10]),
    'field_weed_goosefoot': dict(stiffness=0.6, drag=0.5, shiverDrag=0.08, shiverDirectionality=0.5, specular=0.2, smoothnessMax=0.2,
                                 aoMin=0.55, dryColor=[0.95, 0.92, 0.82], colorNoiseSpread=0.35, puddleSink=0.35, sizeVariation=0.4,
                                 scaleFade=[100000, 15]),
    'field_weed_foxtail': dict(stiffness=0.5, drag=0.6, shiverDrag=0.08, shiverDirectionality=0.6, specular=0.25, smoothnessMax=0.3,
                               aoMin=0.55, dryColor=[0.95, 0.9, 0.75], colorNoiseSpread=0.35, puddleSink=0.25, sizeVariation=0.35,
                               scaleFade=[100000, 15]),
    # полынь не желтеет осенью (серебристая до морозов); зимой — тусклее (seasonKeep 2 → по шейдеру ×(0.85, 0.85, 0.75))
    'edge_wormwood': dict(stiffness=0.7, drag=0.5, shiverDrag=0.06, shiverDirectionality=0.5, specular=0.2, smoothnessMax=0.2,
                          aoMin=0.55, dryColor=[0.95, 0.93, 0.85], colorNoiseSpread=0.3, puddleSink=0.5, sizeVariation=0.33,
                          scaleFade=[100000, 20], seasonKeep=2.0),
}
CARPET_NR = dict(shadows=False, densityInDistance=0.3, densityFalloff=[0.008, 0.003])
# имя ассета, сборщик, настройки, материал, Nature Renderer
ASSETS = [
    ('field_stubble_fresh_01', build_stubble, FRESH_01, 'field_stubble_fresh', dict(CARPET_NR, renderDistance=40.0)),
    ('field_stubble_fresh_02', build_stubble, FRESH_02, 'field_stubble_fresh', dict(CARPET_NR, renderDistance=40.0)),
    ('field_stubble_aged_01', build_stubble, AGED_01, 'field_stubble_aged', dict(CARPET_NR, renderDistance=40.0)),
    ('field_sprouts_01', build_sprouts, SPROUTS, 'field_sprouts', dict(CARPET_NR, renderDistance=40.0)),
    ('field_weed_bindweed_01', build_bindweed, None, 'field_weed_bindweed', dict(renderDistance=40.0, shadows=False, densityInDistance=0.5)),
    ('field_weed_goosefoot_01', build_goosefoot, None, 'field_weed_goosefoot', dict(renderDistance=70.0, shadows=False, densityInDistance=0.5)),
    ('field_weed_foxtail_01', build_foxtail, None, 'field_weed_foxtail', dict(renderDistance=70.0, shadows=False, densityInDistance=0.5)),
    ('edge_wormwood_01', build_wormwood, None, 'edge_wormwood', dict(renderDistance=100.0, shadows=False, densityInDistance=0.6)),
]


def main():
    args = eg.script_args()
    out_dir = args[0]
    only = set(args[1:])
    os.makedirs(out_dir, exist_ok=True)
    report_path = os.path.join(out_dir, 'field_report.json')
    report = {}
    if only and os.path.exists(report_path):
        with open(report_path, encoding='utf-8') as f:
            report = {k: v for k, v in json.load(f).items() if k in {a[0] for a in ASSETS}}
    for name, builder, cfg, material_id, nr in ASSETS:
        if only and name not in only:
            continue
        folder = os.path.join(out_dir, name)
        os.makedirs(folder, exist_ok=True)
        eg.reset_scene()
        build_atlas(folder)
        eg.reset_scene()
        obj, tris, stats = builder(name, zlib.crc32(name.encode()), cfg)
        dims = obj.dimensions
        eg.export_fbx([obj], os.path.join(folder, name + '.fbx'))
        material = dict(BASE_MATERIAL, id=material_id, **MATERIALS[material_id])
        manifest = eg.write_manifest(folder, name, 'detail', 'Field',
                                     [dict(fbx=name + '.fbx', screen=0.0, submeshes=[material_id], castShadows=False)], [material])
        manifest['nr'] = nr
        with open(os.path.join(folder, 'manifest.json'), 'w', encoding='utf-8') as f:
            json.dump(manifest, f, indent=1)
        report[name] = dict(triangles=tris, size=[round(dims.x, 3), round(dims.y, 3), round(dims.z, 3)], **stats)
    with open(report_path, 'w', encoding='utf-8') as f:
        json.dump(report, f, indent=1)
    print('FIELD_OK', json.dumps(report))


main()
