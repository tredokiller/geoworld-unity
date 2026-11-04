# Бурьян заброшенных полей (Tools~/TreeGen, Blender 5.2 фоном): вид поля после боёв (Бахмут, через 1–3 года) — поля,
# брошенные с 2022 года, зарастают не посевами, а рудеральными сорняками. По наблюдениям в Донбассе за 2–3 года брошенное
# поле встаёт зарослью выше метра: амброзия (Ambrosia artemisiifolia), мелколепестник канадский (Erigeron canadensis),
# бодяк, чертополох, татарник (Cirsium, Carduus, Onopordum); с 3–5-го года — полынь (Artemisia absinthium, A. vulgaris,
# серебристо-серо-зелёная). Бурьян зелёный ещё осенью, когда убранные поля уже бурые; поздней осенью и зимой из снега
# торчат мёртвые серо-бурые будылья. С дрона на 10–60 м — грубый неровный покров по колено–грудь с куртинами, метёлками и
# сухими стеблями, не газон и не рядки посевов.
# Как стерня field_cards_gen.py — альфа-карточками: растения живут в текстуре (стебель-полигон в 3 мм тоньше пикселя уже
# с нескольких метров). Каждое растение для запекания — настоящая геометрия (стебли-трубки, перистые, лопастные и
# колючие листья-ленты, кисти, метёлки, корзинки, пух семян); Cycles снимает их ортокамерой:
#  - полосы «вид сбоку» (2048 × 640 / 512 / 384, 2.4–3.6 мм на тексель; высокая — для середины пятна, средняя, низкая —
#    для края; строк по высоте содержимого: низкая в 512 строк была наполовину пустой): срез заросли глубиной 0.45–0.5 м,
#    нарезанный на участки 0.45–1.2 м; растения целиком внутри участка (ни одно не обрезано краем), поэтому карточка,
#    взявшая участок, кончается мягко; у трёх участков из четырёх растения к концам ниже (бугор) — с ровным верхом до края
#    пятно читалось цилиндром-«живой изгородью». Растений столько, что срез непрозрачен на 40–75 % (было 10–25 %: пятно
#    сквозило). Под полосой 16–24 строки повторяют её нижнюю строку — подземная юбка карточки на склоне. В раскладку —
#    высота растений каждого участка (seg_h: карточка не выше своих растений — меньше пустой альфы и перерисовки) и их
#    виды (seg_kinds: середину варианта можно отдать татарнику, мелколепестнику...);
#  - плитки «вид сверху» (4 × 512², 3.1 мм — шатёр r 0.79 м, непрозрачный до ~0.65 м): верх заросли выше z_cut (камера
#    отсекает низ); растения досыпаются, пока круг 0.8 радиуса не закрыт на cover (быстрый рендер альфы 256²: 0.9; у
#    будыльев 0.42 — сверху сквозь них видны земля и снег, у подседа 0.72); край мягкий и рваный, не дальше апофемы
#    восьмиугольника. Без шатров вертикальные карточки сверху видны ребром, а с шатрами r 0.46 м у середины пятна
#    заросль с высоты так и не смыкалась — половина земли между пятнами голая (пятно закрывало сверху ~0.65 м², теперь
#    ~2–2.8 м²). Яркость каждой плитки — 0.74–0.86 верхней четверти полос (сверху видны освещённые верхушки; с AO и
#    множителем 0.72 плитки были вдвое темнее, и заросль над головой чернела; у чертополоха и будыльев ниже: их верх
#    светлят белый пух и сухие корзинки);
#  - растения области — пул из 10–16 неповторимых, расставленных экземплярами Cycles (свой поворот и масштаб): геометрия
#    строится в разы быстрее, повтора в текстуре не видно;
#  - проходы: альбедо (эмиссия цвета вершин), гладкость (альфа цвета вершин), нормаль в пространстве камеры (касательная
#    карточки, OpenGL), AO, высота (у плиток). Альбедо притемнено к низу заросли и по AO (в игре прямой свет солнца на
#    карточку не знает, что низ заросли в тени), затем приведено к палитре сцены (envgen_common.grade_albedo: насыщенность ×
#    saturation, среднее видимых текселей полос — target, линейное); цвет растекается за край альфы (push-pull по
#    пирамиде с нечётными краями: без них низ полос выпадал из грубых уровней, и в атласе оставались чёрные дыры).
# У ассета ОДИН материал и одна текстура (деталь террейна — ровно один материал, DetailPrototype.Validate; импортёр
# сохраняет покрытие альфы в мипах одним множителем на текстуру — поэтому у каждого вида своя).
# Пятно (≤ 200 треугольников — NatureRendererSettings.growTriangleLimit: лёгкие детали растут с ползунком травы):
# 8–14 вертикальных карточек с изломом в плане (2–3 звена), подземной юбкой 5–8 см, каждая со своим случайным поворотом
# (три семейства параллельных карточек разом вставали ребром, пятно читалось осколками) в круге 0.9–1.05 м: 2–4
# крест-накрест в середине, остальные кольцами; к краю ниже, короче (из низкой полосы) и клонятся наружу не больше lean
# (полынь — 6°): низкий край пятна заходит в соседние. Плюс 2–4 «шатра» (восьмиугольник веером, r 0.79 м × размер
# варианта, вершина на 0.1–0.2 роста, край в 1–4 см от земли — сбоку прячется в заросли: с вершиной на 0.3–0.45 роста
# шатры торчали светлыми пирамидами), отнесённые от середины на 0.26–0.44 м — в промежутки, где карточки сверху не
# закрывают землю, но не дальше круга карточек. Вес ветра — альфа цвета вершин: не ниже 0.17 (≥ 40/255: NM-ветер с меньшим
# весом терял треугольники, VegetationImporter.SaveSubmesh), к верху — до wind. Нормали вершин — «сфера кроны»
# normalize(normalize(p − (0, 0, 0.3H)) + 0.5·вверх), у шатров ближе к «вверх»: у куртины солнечный и теневой бок
# (нормаль «грань + вверх» от стерни освещала каждую карточку отдельной плитой).
# 2–3 варианта пятна на ассет — отдельные FBX в своих папках (<имя>, <имя>_b, <имя>_c) с теми же текстурами и тем же
# материалом (в профиль — по записи на префаб, плотность вида поровну): свой план карточек, размер 0.88–1.04, свой
# силуэт — полынь низким широким бугром, амброзия рваным высоким верхом, татарник редкими высокими канделябрами,
# будылья редкими прямыми стеблями с обломанными верхами; одно пятно на ассет давало в поле питомник одинаковых кустов.
# Ассеты (группа Ruderal; материал — роль grass, NM_Foliage NR; windColorMix 0 — оттенок ветра образца травы рыжит):
#  - ruderal_wormwood_stand — полынь горькая 0.8–1.3 м (и обыкновенная 1.4–1.75 м, тёмная, выше бугров): плотные
#    округлые шалфейно-серо-зелёные куртины-бугры, перисторассечённые листья (доли не уже 2 текселей), серо-жёлтые метёлки;
#  - ruderal_ragweed_stand — амброзия и мелколепестник, 0.5–1.9 м: прямые зелёные стебли, дважды перистые листья,
#    бледные колосовидные кисти амброзии, узкие кремовые метёлки мелколепестника — светлый верхний ярус; высоты вразнобой;
#  - ruderal_thistle_clump — татарник (серебристый канделябр до 2.1 м, крылатый, крупные колючие листья), чертополох
#    (поникающие корзинки), бодяк полевой (куртина тонких стеблей): серо-зелёные колючие листья; корзинки к концу лета —
#    половина лиловые, половина бурые или в белом пуху (осенью ассет сохнет в бурое, а не в сиреневые точки);
#  - ruderal_dead_stems — прошлогодние будылья амброзии, мелколепестника, полыни, чертополоха 0.7–1.5 м: тёпло-серо-бурые
#    голые стебли с сухими соцветиями (пух приглушён и вдвое реже: светлые комки пуха читались ватой), часть согнута и
#    сломана (верх висит или лежит). Мёртвое: seasonKeep 2 (по сезону не перекрашивается, зимой серее); в профиле —
#    AboveSnow (зимой стоит над снегом): подсказка AboveSnow в манифесте;
#  - ruderal_mixed_low — низкий подсед 0.3–0.6 м между куртинами: свинорой (пальчатые колоски), подорожник, сухая трава
#    (костёр), розетки; два шатра-купола с рваным краем — главное, что видно с высоты.
# Пора года: полынь и амброзия — seasonKeep 1 (бурьян зелёный и осенью, когда убранные поля уже бурые; зимой их не
# сажают — там будылья, с seasonKeep 1 они стояли бы зелёными на снегу); чертополох и подсед — seasonKeep 0 (осенью
# сохнут: лиловые корзинки уходят в бурое); будылья — seasonKeep 2. Тени Nature Renderer у всех выключены: трава в NR
# отбрасывает тени до 20 м (NatureRendererFade сжимает тени травы с 9 м), у заросли по грудь вокруг дрона вставало тёмное
# кольцо. densityInDistance 0.6 у высоких: с 0.3 компенсация плотности (GameKeptScale) растила дальние пятна в 2.2 раза
# вширь и в 1.5 ввысь — куст 2.8 × 3.3 м на краю прореживания.
# Подсказка профиля (profileHint в манифесте; имена полей — как у FlyPointDetailEntry): DensityPerSquareMeter на вид
# (поровну на его варианты), ClumpScale/ClumpStrength (виды пятнами, а не перцем с солью), LandScales — только Fallow
# (брошенная земля, CraterField.AbandonedAt; без них запись растёт везде), SatelliteScales (на снимке до запустения там
# могла быть пашня — Dark не гасить), SeasonScales (весна в игре — середина апреля: однолетники ещё всходы), AboveSnow.
# Профиль игры этот генератор не трогает.
# Предпросмотр (preview): поле пятен как у GroundDressing + Nature Renderer (ячейки 2 м, плотность × куртины по шуму с
# ClumpScale/ClumpStrength подсказки — формула GroundDressing, SizeVariation по корню, как RotorWash.hlsl), материал как
# NM_Foliage в Unity (порог альфы, мипы с сохранением покрытия, выбор мипа по расстоянию и углу, шум цвета healthy/dry,
# пора года как SeasonFoliage.hlsl, плотность × SeasonScales), свет «game» из field_cards_gen.py; три вида на лист (сверху
# с 30 м, наискось с 15 м, вблизи с 3 м) — <out>/_preview/<имя>_sheet.png; у вида сверху — доля голой земли (земля —
# holdout, альфа кадра) в середине кадра и во всём: <имя>_coverage.json.
# Запуск (Blender 5.2, фоном; --out по умолчанию Tools~/TreeGen/out/Ruderal, --device CPU|CUDA|OPTIX):
#   blender.exe -b --factory-startup --python ruderal_gen.py -- all [--out <папка>] [--assets <имя> ...]
#   blender.exe -b --factory-startup --python ruderal_gen.py -- atlas [--assets ...] [--regions <область> ...]
#   blender.exe -b --factory-startup --python ruderal_gen.py -- assets [--assets ...]
#   blender.exe -b --factory-startup --python ruderal_gen.py -- preview [--assets ...] [--mix вид:экз/м²,...] [--tag имя]
#       [--views top,oblique,close] [--season summer|autumn|winter|spring] [--res 1280x720] [--samples 32]
#       [--density экз/м²] [--clump сила]     (вид — имя ассета: в поле идут все его варианты, плотность поровну)
#   blender.exe -b --factory-startup --python ruderal_gen.py -- plants <out.png> <вид> ...   (растения по одному)
# Импорт в Unity: Tools/Environment/Import Generated Vegetation на папку <out> (VegetationImporter, группа Ruderal).
import argparse
import json
import math
import os
import random
import shutil
import sys
import time
import zlib

import bpy
import numpy as np
from mathutils import Matrix, Vector

# без __pycache__ рядом с генераторами (пакет его не игнорирует)
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import envgen_common as eg  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.join(HERE, 'out', 'Ruderal')
CACHE_DIR = os.path.join(os.environ.get('TEMP') or os.environ.get('TMP') or HERE, 'ruderal_bake')
GROUP = 'Ruderal'
UP = np.array([0.0, 0.0, 1.0])
WIND_MIN = 0.17        # 43/255 ≥ 40/255
TRI_LIMIT = 200
DEVICE = 'CPU'


# ============================================================================================== мелочи
def unit(v):
    v = np.asarray(v, np.float64)
    n = float(np.linalg.norm(v))
    return v / n if n > 1e-12 else v


def rot(axis, angle):
    x, y, z = unit(axis)
    c, s = math.cos(angle), math.sin(angle)
    k = 1.0 - c
    return np.array([[c + x * x * k, x * y * k - z * s, x * z * k + y * s],
                     [y * x * k + z * s, c + y * y * k, y * z * k - x * s],
                     [z * x * k - y * s, z * y * k + x * s, c + z * z * k]])


def perp(d):
    d = unit(d)
    ref = UP if abs(d[2]) < 0.9 else np.array([1.0, 0.0, 0.0])
    return unit(np.cross(d, ref))


def horiz_side(d):
    """Поперёк листа: горизонтально и перпендикулярно d — пластинка смотрит вверх."""
    s = np.cross(UP, d)
    return unit(s) if np.linalg.norm(s) > 1e-6 else np.array([1.0, 0.0, 0.0])


def lerp(a, b, t):
    return a + (b - a) * t


def smoothstep(a, b, x):
    t = np.clip((np.asarray(x, np.float64) - a) / (b - a), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def lin(h):
    return np.array(eg.hex_rgb(h), np.float64)


def rgba(c, a):
    return np.array([c[0], c[1], c[2], a], np.float64)


def jit(rng, c, lum=(0.9, 1.1), hue=0.04):
    c = np.asarray(c, np.float64) * rng.uniform(*lum)
    return np.clip(c * np.array([1 + rng.uniform(-hue, hue) for _ in range(3)]), 0.0, 1.0)


def luminance(c):
    return float(0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2])


def pick(rng, items):
    return items[rng.randrange(len(items))]


# ============================================================================================== геометрия запекания
class Geo:
    """Меш для рендера: вершины, четырёх- и треугольники, цвет на вершину (RGB — альбедо линейно, A — гладкость)."""

    def __init__(self):
        self.V, self.C, self.Q, self.T = [], [], [], []
        self.n = 0

    def add(self, V, C, quads=None, tris=None):
        V = np.asarray(V, np.float64).reshape(-1, 3)
        C = np.asarray(C, np.float64).reshape(-1, 4)
        if quads is not None and len(quads):
            self.Q.append(np.asarray(quads, np.int64).reshape(-1, 4) + self.n)
        if tris is not None and len(tris):
            self.T.append(np.asarray(tris, np.int64).reshape(-1, 3) + self.n)
        self.V.append(V)
        self.C.append(C)
        self.n += len(V)

    def arrays(self):
        V = np.concatenate(self.V) if self.V else np.zeros((0, 3))
        C = np.concatenate(self.C) if self.C else np.zeros((0, 4))
        Q = np.concatenate(self.Q) if self.Q else np.zeros((0, 4), np.int64)
        T = np.concatenate(self.T) if self.T else np.zeros((0, 3), np.int64)
        return V, C, Q, T

    def add_geo(self, other, offset=(0.0, 0.0, 0.0), matrix=None):
        if not other.V:
            return
        V, C, Q, T = other.arrays()
        if matrix is not None:
            V = V @ np.asarray(matrix, np.float64).T
        self.add(V + np.asarray(offset, np.float64), C, Q, T)

    def transform(self, fn):
        self.V = [fn(v) for v in self.V]

    def bounds(self):
        V = np.concatenate(self.V)
        return V.min(0), V.max(0)

    @property
    def faces(self):
        return sum(len(q) for q in self.Q) + sum(len(t) for t in self.T)

    def to_object(self, name, smooth=True):
        ob = bpy.data.objects.new(name, self.to_mesh(name, smooth))
        bpy.context.scene.collection.objects.link(ob)
        return ob

    def to_mesh(self, name, smooth=True):
        V, C, Q, T = self.arrays()
        V = V.astype(np.float32)
        C = C.astype(np.float32)
        loops = np.concatenate([Q.ravel(), T.ravel()]).astype(np.int32)
        sizes = np.concatenate([np.full(len(Q), 4), np.full(len(T), 3)]).astype(np.int32)
        starts = (np.cumsum(sizes) - sizes).astype(np.int32)
        me = bpy.data.meshes.new(name)
        me.vertices.add(len(V))
        me.vertices.foreach_set('co', V.ravel())
        me.loops.add(len(loops))
        me.loops.foreach_set('vertex_index', loops)
        me.polygons.add(len(sizes))
        me.polygons.foreach_set('loop_start', starts)
        me.update(calc_edges=True)
        me.validate(clean_customdata=False)
        if smooth:
            me.polygons.foreach_set('use_smooth', np.ones(len(me.polygons), bool))
        li = np.zeros(len(me.loops), np.int32)
        me.loops.foreach_get('vertex_index', li)
        attr = me.color_attributes.new('Col', 'FLOAT_COLOR', 'CORNER')
        attr.data.foreach_set('color', C[li].ravel())
        return me


def tangents(P):
    T = np.zeros_like(P)
    T[1:-1] = P[2:] - P[:-2]
    T[0] = P[1] - P[0]
    T[-1] = P[-1] - P[-2]
    return T / np.maximum(np.linalg.norm(T, axis=1, keepdims=True), 1e-12)


def tube(geo, P, R, C, sides=5, phase=0.0):
    """Трубка по осевой P (m,3), радиусы R, цвет C (m,4) или (4,); рамка — параллельный перенос (без скручивания)."""
    P = np.asarray(P, np.float64)
    m = len(P)
    if m < 2:
        return
    R = np.broadcast_to(np.asarray(R, np.float64), (m,))
    C = np.asarray(C, np.float64)
    if C.ndim == 1:
        C = np.repeat(C[None], m, 0)
    T = tangents(P)
    N1 = np.zeros_like(P)
    n = perp(T[0])
    for i in range(m):
        n = n - T[i] * float(np.dot(n, T[i]))
        nn = float(np.linalg.norm(n))
        n = n / nn if nn > 1e-9 else perp(T[i])
        N1[i] = n
    N2 = np.cross(T, N1)
    th = phase + np.arange(sides) * (2 * math.pi / sides)
    off = np.cos(th)[None, :, None] * N1[:, None, :] + np.sin(th)[None, :, None] * N2[:, None, :]
    V = P[:, None, :] + R[:, None, None] * off
    idx = np.arange(m * sides).reshape(m, sides)
    j1 = np.roll(np.arange(sides), -1)
    quads = np.stack([idx[:-1, :], idx[:-1, j1], idx[1:, j1], idx[1:, :]], -1).reshape(-1, 4)
    geo.add(V.reshape(-1, 3), np.repeat(C, sides, axis=0), quads)


def ribbon(geo, P, W, S, C, fold=0.0):
    """Лента (лист): осевая P (m,3), полуширина W, поперечное S (m,3), цвет C (m,4); fold — подъём жилки в долях ширины."""
    P = np.asarray(P, np.float64)
    W = np.maximum(np.asarray(W, np.float64), 0.00015)
    S = np.asarray(S, np.float64)
    C = np.asarray(C, np.float64)
    if C.ndim == 1:
        C = np.repeat(C[None], len(P), 0)
    T = tangents(P)
    N = np.cross(T, S)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    left = P - S * W[:, None]
    right = P + S * W[:, None]
    mid = P + N * (fold * W)[:, None]
    m = len(P)
    V = np.stack([left, mid, right], 1).reshape(-1, 3)
    idx = np.arange(m * 3).reshape(m, 3)
    quads = [np.stack([idx[:-1, k], idx[:-1, k + 1], idx[1:, k + 1], idx[1:, k]], -1) for k in range(2)]
    geo.add(V, np.repeat(C, 3, axis=0), np.concatenate(quads))


def strand(geo, p0, d, length, col, w=0.0005, smooth=0.15):
    """Тонкий штрих (колючка, тычинка, волосок пуха): лента из двух точек, к концу сужается."""
    d = unit(d)
    s = perp(d)
    ribbon(geo, np.array([p0, np.asarray(p0) + d * length]), np.array([w, w * 0.35]), np.array([s, s]),
           np.array([rgba(col, smooth)] * 2))


def ellipsoid(geo, c, axes, col_lo, col_hi=None, nu=6, nv=4, smooth=0.2, rng=None, bump=0.0):
    """Эллипсоид: axes — (3,3), столбцы — полуоси x, y, z; цвет от низа (col_lo) к верху (col_hi) по локальной z."""
    A = np.asarray(axes, np.float64)
    th = np.linspace(0, math.pi, nv + 1)[1:-1]
    ph = np.arange(nu) * (2 * math.pi / nu)
    L = [(math.sin(t) * math.cos(p), math.sin(t) * math.sin(p), math.cos(t)) for t in th for p in ph]
    L += [(0.0, 0.0, 1.0), (0.0, 0.0, -1.0)]
    L = np.array(L)
    if bump and rng is not None:
        L = L * (1 + bump * (np.array([rng.random() for _ in range(len(L))]) - 0.5))[:, None]
    V = np.asarray(c, np.float64) + L @ A.T
    hi = col_lo if col_hi is None else col_hi
    tz = np.clip((L[:, 2] + 1) * 0.5, 0, 1)
    C = np.array([rgba(col_lo + (hi - col_lo) * z, smooth) for z in tz])
    rings = len(th)
    quads = []
    for i in range(rings - 1):
        for j in range(nu):
            a, b = i * nu + j, i * nu + (j + 1) % nu
            quads.append((a, b, b + nu, a + nu))
    top, bot = len(L) - 2, len(L) - 1
    k = (rings - 1) * nu
    tris = [(top, (j + 1) % nu, j) for j in range(nu)] + [(bot, k + j, k + (j + 1) % nu) for j in range(nu)]
    geo.add(V, C, quads if quads else None, tris)


def sphere_axes(r, rz=None, d=None):
    if d is None:
        return np.diag([r, r, r if rz is None else rz])
    d = unit(d)
    e1 = perp(d)
    e2 = np.cross(d, e1)
    return np.stack([e1 * r, e2 * r, d * (r if rz is None else rz)], 1)


class Curve:
    """Ломаная с параметром по длине: at(t) -> (точка, касательная)."""

    def __init__(self, P):
        self.P = np.asarray(P, np.float64)
        seg = np.linalg.norm(np.diff(self.P, axis=0), axis=1)
        self.s = np.concatenate([[0.0], np.cumsum(seg)])
        self.L = float(self.s[-1])

    def at(self, t):
        x = min(max(t, 0.0), 1.0) * self.L
        i = int(min(max(np.searchsorted(self.s, x) - 1, 0), len(self.P) - 2))
        seg = self.s[i + 1] - self.s[i]
        f = (x - self.s[i]) / seg if seg > 1e-12 else 0.0
        return self.P[i] + (self.P[i + 1] - self.P[i]) * f, unit(self.P[i + 1] - self.P[i])


def grow(rng, base, d0, length, n, bend_axis=None, bend=0.0, gravity=0.0, wobble=0.0):
    """Изогнутая ось: шаг за шагом направление поворачивается вокруг bend_axis на bend/n (bend_axis = cross(UP, out) —
    дугой наружу, cross(d, UP) — к вертикали), тянется вниз gravity и случайно изламывается на wobble (рад)."""
    P = [np.asarray(base, np.float64)]
    d = unit(d0)
    step = length / n
    for _ in range(n):
        if bend and bend_axis is not None and np.linalg.norm(bend_axis) > 1e-9:
            d = rot(bend_axis, bend / n) @ d
        if gravity:
            d = unit(d - UP * gravity * step)
        if wobble:
            ax = rot(d, rng.uniform(0, 2 * math.pi)) @ perp(d)
            d = rot(ax, rng.gauss(0.0, wobble)) @ d
        P.append(P[-1] + d * step)
    return np.array(P)


def leaf_dir(stem_d, az, angle):
    """Направление под углом angle к оси стебля по азимуту az вокруг неё."""
    e1 = perp(stem_d)
    e2 = np.cross(unit(stem_d), e1)
    o = math.cos(az) * e1 + math.sin(az) * e2
    return unit(unit(stem_d) * math.cos(angle) + o * math.sin(angle))


def w_lanceolate(t):
    return max(0.06, math.sin(math.pi * min(max(t, 0.0), 1.0) ** 0.8)) * (1 - 0.2 * t)


def w_linear(t):
    return max(0.05, (1 - t ** 3) * min(1.0, 0.4 + t * 4))


def w_lobed(k, depth, env=w_lanceolate):
    return lambda t: max(0.05, env(t) * (1 - depth * (0.5 - 0.5 * math.cos(2 * math.pi * k * t))))


def leaf_axis(base, d, L, n, droop):
    p = np.asarray(base, np.float64).copy()
    dd = unit(d)
    pts = []
    for i in range(n):
        pts.append(p.copy())
        ax = np.cross(dd, UP)
        if np.linalg.norm(ax) < 1e-6:
            ax = perp(dd)
        dd = rot(ax, -droop / max(n - 1, 1)) @ dd
        p = p + dd * (L / max(n - 1, 1))
    return np.array(pts)


def leaf_sides(T, plane=None, roll=0.0):
    """Поперечные векторы листа по касательным T: в плоскости с нормалью plane (доля перистого листа лежит в плоскости
    листа) или горизонтально, повёрнутые вокруг жилки на roll. Листья с одной лишь горизонтальной пластинкой с камеры
    сбоку видны ребром — заросль на полосе выходила голыми палками."""
    out = []
    for t in T:
        s = np.cross(plane, t) if plane is not None else np.cross(UP, t)
        s = unit(s) if np.linalg.norm(s) > 1e-6 else horiz_side(t)
        out.append(rot(t, roll) @ s if roll else s)
    return np.array(out)


def blade(geo, rng, base, d, L, W, cols, n=5, droop=0.4, wfn=w_lanceolate, twist=0.0, fold=0.3, smooth=0.25, wave=0.0,
          waves=0, roll=0.0, plane=None):
    """Лист-лента: жилка из base по d, к кончику клонится вниз на droop (рад); полуширина W·wfn(t); пластинка
    повёрнута вокруг жилки на roll (или лежит в плоскости plane); wave — край волной (±wave, waves волн); cols —
    (цвет основания, кончика)."""
    P = leaf_axis(base, d, L, n, droop)
    T = tangents(P)
    S0 = leaf_sides(T, plane, roll)
    c0, c1 = cols
    ws, ss, cs = [], [], []
    for i in range(n):
        t = i / (n - 1)
        ws.append(W * wfn(t))
        a = twist * t + (wave * math.sin(2 * math.pi * waves * t) if waves else 0.0)
        ss.append(rot(T[i], a) @ S0[i] if a else S0[i])
        cs.append(rgba(c0 + (c1 - c0) * t, smooth))
    ribbon(geo, P, np.array(ws), np.array(ss), np.array(cs), fold)
    return P, np.array(ws), np.array(ss)


def pinnate(geo, rng, base, d, L, pairs, lobe_frac, lobe_w, cols, rachis_w, droop=0.5, angle=(40, 65), sub=0,
            smooth=0.25, start=0.22, lobe_droop=0.35, roll=None, plane=None, lobe_wfn=w_lanceolate):
    """Перистый лист: стержень и пары долей в плоскости листа (sub > 0 — доли сами перистые: дважды рассечённый лист
    полыни, амброзии); плоскость повёрнута вокруг стержня на roll (по умолчанию случайно ±70°)."""
    if roll is None:
        roll = math.radians(rng.uniform(-70, 70))
    n = max(4, pairs + 3)
    P = leaf_axis(base, d, L, n, droop)
    T = tangents(P)
    S = leaf_sides(T, plane, roll if plane is None else 0.0)
    c0, c1 = cols
    t_ = np.linspace(0, 1, n)
    ribbon(geo, P, rachis_w * (1 - 0.6 * t_), S, np.array([rgba(c0 + (c1 - c0) * t, smooth) for t in t_]), 0.0)
    crv = Curve(P)
    for k in range(pairs):
        t = start + (1 - start) * (k + 0.5) / (pairs + 0.5)
        p, tt = crv.at(t)
        i = min(n - 1, int(round(t * (n - 1))))
        s = S[i]
        npl = unit(np.cross(tt, s))
        f = 1.15 - 0.7 * t
        cl = c0 + (c1 - c0) * t
        for sign in (1, -1):
            a = math.radians(rng.uniform(*angle))
            ld = unit(tt * math.cos(a) + s * sign * math.sin(a))
            ll = L * lobe_frac * f * rng.uniform(0.85, 1.1)
            if sub:
                pinnate(geo, rng, p, ld, ll, sub, 0.45, lobe_w * 0.85, (cl, cl), rachis_w * 0.6, droop=lobe_droop,
                        angle=(35, 55), sub=0, smooth=smooth, start=0.3, lobe_droop=0.25, plane=npl, lobe_wfn=lobe_wfn)
            else:
                blade(geo, rng, p, ld, ll, lobe_w, (cl, cl), n=3, droop=lobe_droop, fold=0.2, smooth=smooth, plane=npl,
                      wfn=lobe_wfn)
    tt = unit(P[-1] - P[-2])
    blade(geo, rng, P[-1] - tt * 0.002, tt, L * lobe_frac * 0.7, lobe_w, (c1, c1), n=3, droop=lobe_droop, smooth=smooth,
          plane=unit(np.cross(tt, S[-1])), wfn=lobe_wfn)


_TEMPLATES = {}
BLACK = np.zeros(3)
WHITE = np.ones(3)


def leaf_templates(key, builder, count=10):
    """Заготовки сложного листа (дважды-трижды рассечённого): строятся один раз — лист длиной 1 по +X, пластинка
    горизонтально, цвет R = доля длины (t) — и ставятся одной матрицей. Полынь из 10 тысяч рекурсивных листьев на куст
    строилась по полминуты."""
    if key not in _TEMPLATES:
        rng = random.Random(zlib.crc32(key.encode()))
        items = []
        for _ in range(count):
            g = Geo()
            builder(g, rng)
            items.append(g.arrays())
        _TEMPLATES[key] = items
    return _TEMPLATES[key]


def place_leaf(geo, rng, templates, base, d, L, cols, roll=None):
    """Заготовка листа в точку base по направлению d, длина L, пластинка повёрнута вокруг жилки на roll (по умолчанию
    случайно ±70°), цвет — от cols[0] у основания к cols[1] у кончика."""
    V, C, Q, T = templates[rng.randrange(len(templates))]
    d = unit(d)
    if roll is None:
        roll = math.radians(rng.uniform(-70, 70))
    s = rot(d, roll) @ horiz_side(d)
    n = np.cross(d, s)
    M = np.stack([d, s, n], 1) * L
    c0, c1 = cols
    t = C[:, 0:1]
    geo.add(V @ M.T + np.asarray(base), np.concatenate([c0 + (c1 - c0) * t, C[:, 3:4]], 1), Q, T)


def fluff(geo, rng, c, r, col, strands=24, smooth=0.1):
    """Пух семян: рыхлый шар и торчащие волоски (с 10 м — белое пятнышко, вблизи — пушистое)."""
    ellipsoid(geo, c, sphere_axes(r * 0.55), col * 0.86, col, nu=7, nv=5, smooth=smooth, rng=rng, bump=0.4)
    for _ in range(strands):
        v = unit(np.array([rng.gauss(0, 1), rng.gauss(0, 1), rng.gauss(0, 1) + 0.5]))
        strand(geo, np.asarray(c) + v * r * 0.4, v, r * rng.uniform(0.5, 0.8), col * rng.uniform(0.92, 1.04), w=0.0006,
               smooth=smooth)


def stem_colours(n, c_base, c_top, shade, smooth=0.2, reach=1.6):
    return np.array([rgba(lerp(c_base, c_top, min(1.0, i / max(n - 1, 1) * reach)) * shade, smooth) for i in range(n)])


# ============================================================================================== палитры (sRGB)
def pal(d):
    """Палитра: строки — один цвет, списки — варианты; всё в линейный цвет."""
    out = {}
    for k, v in d.items():
        if isinstance(v, str):
            out[k] = lin(v)
        elif isinstance(v, (list, tuple)) and v and isinstance(v[0], str):
            out[k] = [lin(c) for c in v]
        else:
            out[k] = v
    return out


WW_ABS = pal(dict(leaf=['#8F9E84', '#9AA88E', '#84937A', '#95A28A'], under='#B2BAA6', stem_lo='#76735E', stem_hi='#96A08A',
                  head=['#BDB585', '#B2AA7C', '#C6BD8A', '#B0AB84'], late='#8C8068', branch='#99A08A', smooth=0.3))
WW_VULG = pal(dict(leaf=['#55663F', '#5F7047', '#4E5F3E'], under='#A7AC9A', stem_lo='#5E4438', stem_hi='#7A5C4C',
                   head=['#9A7E62', '#A68A6C', '#8E745A'], late='#6E5A4A', branch='#7A6050', smooth=0.25))
RAGWEED = pal(dict(leaf=['#4E6A34', '#577338', '#5F7A3E', '#4B6332'], dried=['#8A8458', '#7E7650', '#968C5E'],
                   stem_lo='#6A5A40', stem_hi='#5E7442', axis='#88955A',
                   heads=['#B6BA74', '#C0C07E', '#AEB36E', '#C8C488'], smooth=0.3))
FLEABANE = pal(dict(leaf=['#6C8746', '#76904E', '#647E42'], dried=['#8E825C', '#9A8E66'], stem_lo='#8A7E58',
                    stem_hi='#7C9054', heads=['#D6D0B4', '#CFC9AC', '#DDD8C2', '#C9C3A2'], fluff='#DEDAD0',
                    branch='#8C9862', smooth=0.25))
CIRSIUM = pal(dict(leaf=['#6B7B58', '#738360', '#64744F'], under='#8E9A7E', stem_lo='#6A684C', stem_hi='#6F7E55',
                   spine='#CFC596', invol_lo='#58623F', invol_hi='#7A8058', flower=['#A85CB8', '#9852AC', '#B672C2'],
                   fluff='#E6E3DA', dry='#7A6450', smooth=0.25))
CARDUUS = pal(dict(leaf=['#627250', '#6C7B57', '#5C6B4A'], under='#86927A', stem_lo='#5E6646', stem_hi='#6A7A52',
                   wing='#687852', spine='#D3C99A', invol_lo='#56603F', invol_hi='#7C8458',
                   flower=['#9E4A7C', '#A8578A', '#904272'], fluff='#E4E0D6', dry='#6E5A46', smooth=0.25))
ONOPORDUM = pal(dict(leaf=['#9DA796', '#A7B0A0', '#949E8C'], under='#B4BAAC', stem_lo='#8A9080', stem_hi='#A2AA9A',
                     wing='#A0A898', spine='#D6CFAA', invol_lo='#7E8676', invol_hi='#9CA292',
                     flower=['#8C5EA4', '#9A6AB0', '#7E5296'], fluff='#E2DED6', dry='#7A6A56', smooth=0.2))
# будылья: тёпло-серо-бурые (r/b ~1.3–1.45 — серые читались сиреневыми рядом с зелёной амброзией и серыми на снегу),
# низ стебля не темнее #6A6253 (тёмная черта у линии снега), пух приглушён до #8E887C (светлые комки пуха с 15–30 м
# читались ватой и забивали всё остальное)
DEAD = pal(dict(stalk=['#8C7F69', '#7E715C', '#978A73', '#706653', '#877962', '#9C8F7A'], base='#6A6253',
                heads=['#6E604C', '#655845', '#7A6B55', '#5E5446'], fluff='#8E887C', leaf='#6A6152', smooth=0.15))
DRIED_LEAF = lin('#8E8472')
LOW = pal(dict(grass=['#6E7C55', '#788659', '#65744E', '#7F8A5C'], dry=['#A39A76', '#9A8F6C', '#ADA37E', '#8F876A'],
               plantain=['#4E6438', '#56693E', '#5C6E44'], spike='#5E5040', stamen='#CFC8B0',
               cyn_spike=['#6E5A55', '#7A6A58', '#8E8264'], herb=['#5E7244', '#687A4A'], smooth=0.25))


# ============================================================================================== растения
def wormwood_tuft(rng, H=1.1, width=1.0, vulgaris=False, late=0.0, dead=0):
    """Полынь — куртина-бугор: стебли из основания 0.1–0.2 м (у обыкновенной — уже), у краевых — наклон наружу у земли
    (до 12°) и изгиб обратно к вертикали; краевые ниже — верх округлый. Листья по всему стеблю, внизу крупные дважды-
    трижды рассечённые (нижние не пропускаются: куртина густая до земли и шире всего на середине высоты), к метёлке
    мельче; верхние две пятых — метёлки поникающих корзинок 3–4 мм, серо-жёлтые «шпили» над бугром. Обыкновенная
    (vulgaris) — выше (1.4–1.8 м), стеблей меньше и прямее, листья тёмные сверху и белёсые снизу, стебли бурые, метёлка
    узкая. Прежняя куртина — 18–30 стеблей из 3–8 см веером наружу без нижних листьев — в каждом участке полосы стояла
    букетом-«V», и пятно с высоты читалось серыми осколками."""
    P_ = WW_VULG if vulgaris else WW_ABS
    g = Geo()
    # доли листа не уже ~2 текселей полосы (5–9 мм у листа 7–13 см): тоньше — серая каша вблизи
    big = leaf_templates('ww_big', lambda tg, tr: pinnate(tg, tr, (0, 0, 0), (1, 0, 0), 1.0, 3, 0.5, 0.042, (BLACK, WHITE),
                                                           0.02, droop=0.6, sub=2, smooth=0.28, lobe_wfn=w_linear,
                                                           roll=0.0))
    small = leaf_templates('ww_small', lambda tg, tr: pinnate(tg, tr, (0, 0, 0), (1, 0, 0), 1.0, tr.randint(1, 2), 0.55,
                                                               0.075, (BLACK, WHITE), 0.035, droop=0.35, sub=0,
                                                               smooth=0.28, lobe_wfn=w_linear, roll=0.0))
    if vulgaris:
        n_stems = max(5, int(rng.uniform(8, 13) * width))
        rb = (0.05 + 0.06 * width) * rng.uniform(0.85, 1.15)
        lean_max, drop, pan, step = 6.0, 0.15, 0.62, 0.014
    else:
        n_stems = max(9, int(rng.uniform(22, 32) * width))
        rb = (0.08 + 0.12 * width) * rng.uniform(0.85, 1.15)
        lean_max, drop, pan, step = 14.0, 0.42, 0.62, 0.013
    sm = P_['smooth']
    for k in range(n_stems):
        a = rng.uniform(0, 2 * math.pi)
        rho = math.sqrt(rng.random())
        base = np.array([rb * rho * math.cos(a), rb * rho * math.sin(a), -0.02])
        az = a + rng.uniform(-0.35, 0.35)
        out = np.array([math.cos(az), math.sin(az), 0.0])
        lean0 = math.radians(rng.uniform(2.0, 4.0) + lean_max * rho)
        # краевые стебли заметно ниже средних — верх бугра округлый (одинаковые стебли ставили куртину столбом)
        h = H * (1.0 - drop * rho ** 1.2 * rng.uniform(0.6, 1.0)) * rng.uniform(0.93, 1.04)
        L = (h + 0.02) / math.cos(lean0 * 0.5)
        d0 = unit(UP * math.cos(lean0) + out * math.sin(lean0))
        # изгиб к вертикали (дугой наружу стебли раскрывали куртину веером)
        P = grow(rng, base, d0, L, 10, bend_axis=np.cross(UP, out), bend=-lean0 * rng.uniform(0.5, 0.8), wobble=0.025)
        shade = rng.uniform(0.92, 1.06)
        tube(g, P, np.linspace(0.0034, 0.0011, len(P)) * rng.uniform(0.85, 1.15),
             stem_colours(len(P), P_['stem_lo'], P_['stem_hi'], shade), sides=5, phase=rng.random())
        crv = Curve(P)
        lc = pick(rng, P_['leaf']) * rng.uniform(0.93, 1.07)
        # листья по всему стеблю (полынь облиствена до верха): внизу крупные, торчат почти вбок — бугор шире всего на
        # середине и густ до земли; к метёлке мельче
        n_leaves = int(crv.L / step)
        for i in range(n_leaves):
            t = 0.03 + 0.94 * (i + rng.random() * 0.6) / n_leaves
            p, d = crv.at(t)
            col = (P_['under'] if rng.random() < (0.35 if vulgaris else 0.22) else lc) * rng.uniform(0.92, 1.08)
            if t < 0.2:
                col = lerp(col, P_['late'], 0.3 * rng.random())   # нижние листья старше и тусклее
            ld = leaf_dir(d, i * 2.4 + rng.uniform(-0.3, 0.3),
                          math.radians(rng.uniform(55, 85) if t < 0.35 else rng.uniform(35, 65)))
            if t < pan:
                size = rng.uniform(0.08, 0.14) * (1.3 - 0.8 * t) * (1.1 if vulgaris else 1.0)
                place_leaf(g, rng, big, p, ld, size, (col * 0.92, col))
            else:
                place_leaf(g, rng, small, p, ld, rng.uniform(0.03, 0.055) * (1.4 - 0.6 * t), (col, col))
        # метёлка: ветви верхних двух пятых — облиственные кисти поникающих корзинок (сверху и издали верх куста —
        # серо-жёлтая «пена» метёлок, а не голые прутья-«метла»); у обыкновенной ветви короче — метёлка узкая
        n_br = rng.randint(12, 18) if vulgaris else rng.randint(14, 20)
        for j in range(n_br + 1):
            if j < n_br:
                t = pan - 0.06 + (1.0 - pan + 0.03) * (j + rng.random()) / n_br
                p, d = crv.at(t)
                bl = rng.uniform(0.06, 0.15) * (1.5 - t) * min(1.2, H) * (0.55 if vulgaris else 1.0)
                bd = leaf_dir(d, j * 2.4 + rng.uniform(-0.4, 0.4),
                              math.radians(rng.uniform(20, 36) if vulgaris else rng.uniform(30, 55)))
                Pb = grow(rng, p, bd, bl, 4, bend_axis=np.cross(bd, UP), bend=0.3, wobble=0.06)
                tube(g, Pb, np.linspace(0.0012, 0.0006, len(Pb)), rgba(P_['branch'] * shade, 0.2), sides=4)
                cb = Curve(Pb)
            else:
                cb = Curve(P[-4:])
            nh = rng.randint(12, 18)
            for q in range(nh):
                ph, dh = cb.at(0.1 + 0.9 * q / max(nh - 1, 1))
                hc = P_['late'] if (late and rng.random() < late) else pick(rng, P_['head'])
                hc = hc * rng.uniform(0.9, 1.08)
                rh = rng.uniform(0.0022, 0.0032) * (0.8 if vulgaris else 1.0)
                o = rot(dh, rng.uniform(0, 2 * math.pi)) @ perp(dh)
                # корзинка в 1–2 текселя: октаэдра хватает (5 тысяч корзинок на куст)
                ellipsoid(g, ph + o * rh * 1.2 - UP * rh * 0.8, np.diag([rh, rh, rh * (1.3 if vulgaris else 0.9)]),
                          hc * 0.85, hc, nu=4, nv=2, smooth=0.2)
            for q in range(rng.randint(6, 10)):
                ph, dh = cb.at(rng.uniform(0.0, 0.8))
                col = (P_['under'] if rng.random() < 0.3 else lc) * rng.uniform(0.92, 1.08)
                blade(g, rng, ph, leaf_dir(dh, rng.uniform(0, 2 * math.pi), math.radians(rng.uniform(30, 60))),
                      rng.uniform(0.015, 0.035), 0.0022, (col, col), n=3, droop=0.3, wfn=w_linear, smooth=sm,
                      roll=math.radians(rng.uniform(-70, 70)))
    for _ in range(dead):
        g.add_geo(dead_stalk(rng, H * rng.uniform(0.85, 1.1), kind='wormwood', broken=0.3),
                  (rng.uniform(-rb, rb), rng.uniform(-rb, rb), 0.0))
    return g


def raceme(g, rng, base, d, L, axis_c, heads, smooth=0.25, spacing=0.0042, head_r=(0.0022, 0.0031)):
    """Колосовидная кисть амброзии: ось и поникающие корзинки-чашечки по ней."""
    n = max(3, int(L / 0.025) + 2)
    P = grow(rng, base, d, L, n - 1, wobble=0.05)
    tube(g, P, np.linspace(0.0011, 0.0005, len(P)), rgba(axis_c, smooth), sides=4)
    crv = Curve(P)
    k = max(3, int(L / spacing))
    for i in range(k):
        t = 0.08 + 0.92 * i / (k - 1)
        p, dd = crv.at(t)
        o = rot(dd, rng.uniform(0, 2 * math.pi)) @ perp(dd)
        r = rng.uniform(*head_r) * (1.0 - 0.35 * t)
        col = jit(rng, pick(rng, heads), (0.9, 1.08), 0.03)
        ellipsoid(g, p + o * r * 0.9 - UP * r * 0.5, np.diag([r, r, r * 0.6]), col * 0.8, col, nu=5, nv=3, smooth=smooth)


def ragweed(rng, H=1.4, dried_share=0.5):
    """Амброзия полыннолистная: прямой ветвистый стебель, дважды перисторассечённые листья (нижние подсыхают),
    на верхушках стебля и ветвей — пучки колосовидных кистей бледно-жёлто-зелёных корзинок."""
    g = Geo()
    lean = math.radians(rng.uniform(0, 7))
    az = rng.uniform(0, 2 * math.pi)
    out = np.array([math.cos(az), math.sin(az), 0.0])
    P = grow(rng, (0, 0, -0.02), unit(UP * math.cos(lean) + out * math.sin(lean)), H * 1.02, 12,
             bend_axis=np.cross(UP, out), bend=math.radians(rng.uniform(-4, 8)), wobble=0.035)
    shade = rng.uniform(0.92, 1.06)
    r0 = 0.0035 + 0.0015 * H
    tube(g, P, np.linspace(r0, 0.0012, len(P)), stem_colours(len(P), RAGWEED['stem_lo'], RAGWEED['stem_hi'], shade),
         sides=6, phase=rng.random())
    crv = Curve(P)
    lc = pick(rng, RAGWEED['leaf']) * rng.uniform(0.92, 1.08)
    sm = RAGWEED['smooth']
    big = leaf_templates('rg_big', lambda tg, tr: pinnate(tg, tr, (0, 0, 0), (1, 0, 0), 1.0, 5, 0.44, 0.032, (BLACK, WHITE),
                                                           0.0215, droop=0.6, sub=2, smooth=sm, lobe_droop=0.25,
                                                           roll=0.0))
    dry = leaf_templates('rg_dry', lambda tg, tr: pinnate(tg, tr, (0, 0, 0), (1, 0, 0), 1.0, 5, 0.44, 0.032, (BLACK, WHITE),
                                                           0.0215, droop=1.1, sub=2, smooth=0.2, lobe_droop=0.5,
                                                           roll=0.0))
    small = leaf_templates('rg_small', lambda tg, tr: pinnate(tg, tr, (0, 0, 0), (1, 0, 0), 1.0, 4, 0.44, 0.05,
                                                               (BLACK, WHITE), 0.026, droop=0.55, sub=1, smooth=sm,
                                                               roll=0.0))
    # листья 6–15 см, дважды перисторассечённые, доли 4–7 мм: заросль амброзии — сплошная резная зелень
    n_leaves = int(rng.uniform(32, 42) * H)
    for i in range(n_leaves):
        t = 0.06 + 0.88 * (i + rng.random() * 0.5) / n_leaves
        if t < 0.15 and rng.random() < 0.35:
            continue
        p, d = crv.at(t)
        dried = t < 0.3 and rng.random() < dried_share
        col = (pick(rng, RAGWEED['dried']) if dried else lc) * rng.uniform(0.92, 1.08)
        size = rng.uniform(0.1, 0.17) * (1.15 - 0.5 * t) * math.sqrt(H / 1.4)
        ld = leaf_dir(d, i * 2.4 + rng.uniform(-0.3, 0.3), math.radians(rng.uniform(45, 75)))
        place_leaf(g, rng, dry if dried else (big if size > 0.06 else small), p, ld, size, (col * 0.92, col))
    tips = []
    n_br = rng.randint(6, 11)
    for j in range(n_br):
        t = 0.2 + 0.68 * (j + rng.random()) / n_br
        p, d = crv.at(t)
        bl = H * rng.uniform(0.22, 0.42) * (1.25 - 0.75 * t)
        bd = leaf_dir(d, j * 2.4 + rng.uniform(-0.4, 0.4), math.radians(rng.uniform(32, 52)))
        Pb = grow(rng, p, bd, bl, 6, bend_axis=np.cross(bd, UP), bend=math.radians(rng.uniform(10, 30)), wobble=0.05)
        tube(g, Pb, np.linspace(r0 * 0.5, 0.0009, len(Pb)), rgba(RAGWEED['stem_hi'] * shade, 0.25), sides=4)
        cb = Curve(Pb)
        for q in range(rng.randint(6, 10)):
            ph, dh = cb.at(rng.uniform(0.1, 0.85))
            place_leaf(g, rng, small, ph, leaf_dir(dh, rng.uniform(0, 2 * math.pi), math.radians(rng.uniform(40, 70))),
                       rng.uniform(0.05, 0.09), (lc * 0.95, lc))
        tips.append((Pb[-1], unit(Pb[-1] - Pb[-2]), rng.randint(2, 4), 0.7))
    tips.append((P[-1], unit(P[-1] - P[-2]), rng.randint(5, 8), 1.0))
    # пучки кистей на верхушках — светлый верхний ярус заросли (с 15 м он и читается «колосками», а не зелёной массой)
    for p, d, count, scale in tips:
        for q in range(count):
            dd = leaf_dir(d, rng.uniform(0, 2 * math.pi), math.radians(rng.uniform(0, 30) if q else 0.0))
            raceme(g, rng, p, dd, rng.uniform(0.06, 0.15) * scale * min(1.2, H / 1.2), RAGWEED['axis'],
                   RAGWEED['heads'], smooth=0.25, head_r=(0.0025, 0.0035))
    return g


def fleabane(rng, H=1.3, forks=0.2):
    """Мелколепестник канадский: одиночный прямой стебель, густо одетый узкими листьями (нижние засохли или опали),
    верхняя треть — узкая метёлка-«ёлочка» из сотен мелких кремово-белых корзинок (часть уже в пуху)."""
    g = Geo()
    lean = math.radians(rng.uniform(0, 5))
    az = rng.uniform(0, 2 * math.pi)
    out = np.array([math.cos(az), math.sin(az), 0.0])
    P = grow(rng, (0, 0, -0.02), unit(UP * math.cos(lean) + out * math.sin(lean)), H * 1.02, 12,
             bend_axis=np.cross(UP, out), bend=math.radians(rng.uniform(-3, 6)), wobble=0.03)
    shade = rng.uniform(0.92, 1.06)
    tube(g, P, np.linspace(0.0025 + 0.0012 * H, 0.001, len(P)),
         stem_colours(len(P), FLEABANE['stem_lo'], FLEABANE['stem_hi'], shade, reach=2.5), sides=5, phase=rng.random())
    crv = Curve(P)
    pan = rng.uniform(0.3, 0.45)
    t0 = 1 - pan
    lc = pick(rng, FLEABANE['leaf']) * rng.uniform(0.93, 1.07)
    # листья густо, через 0.8–1.3 см: узкие 4–9 см, торчат вверх-наружу — стебель одет зелёной «щёткой»
    n_leaves = int(crv.L * (t0 + 0.08) / rng.uniform(0.008, 0.013))
    for i in range(n_leaves):
        t = 0.05 + (t0 + 0.03) * (i + rng.random() * 0.6) / n_leaves
        if t < 0.2 and rng.random() < 0.55:
            continue
        p, d = crv.at(t)
        dried = t < 0.28
        col = (pick(rng, FLEABANE['dried']) if dried else lc) * rng.uniform(0.92, 1.08)
        L = rng.uniform(0.045, 0.09) * (1.25 - 0.75 * t)
        blade(g, rng, p, leaf_dir(d, i * 2.4 + rng.uniform(-0.3, 0.3), math.radians(rng.uniform(20, 50))), L,
              rng.uniform(0.003, 0.0055), (col * 0.95, col), n=4, droop=rng.uniform(0.3, 1.0) + (0.9 if dried else 0.0),
              wfn=w_lanceolate, smooth=FLEABANE['smooth'], roll=math.radians(rng.uniform(-80, 80)), twist=0.6)
    tops = [(crv, t0)]
    if rng.random() < forks:
        p, d = crv.at(t0)
        for _ in range(rng.randint(1, 2)):
            dd = leaf_dir(d, rng.uniform(0, 2 * math.pi), math.radians(rng.uniform(15, 30)))
            Pf = grow(rng, p, dd, crv.L * pan * rng.uniform(0.7, 0.95), 6, bend_axis=np.cross(dd, UP), bend=0.3,
                      wobble=0.03)
            tube(g, Pf, np.linspace(0.0014, 0.0008, len(Pf)), rgba(FLEABANE['stem_hi'] * shade, 0.2), sides=4)
            tops.append((Curve(Pf), 0.05))
    fluffy = rng.random() * 0.5
    # метёлка густая (ветви через 0.9–1.5 см): кремовый «шпиль» над зеленью виден и с высоты
    for cv, ts in tops:
        span = cv.L * (1 - ts)
        n_br = max(6, int(span / rng.uniform(0.009, 0.015)))
        for j in range(n_br):
            t = ts + (1 - ts) * (j + rng.random() * 0.7) / n_br
            p, d = cv.at(t)
            u = (t - ts) / max(1 - ts, 1e-6)
            bl = (0.02 + rng.uniform(0.05, 0.11) * (1 - u) ** 0.8) * min(1.1, H / 1.3)
            bd = leaf_dir(d, j * 2.4 + rng.uniform(-0.3, 0.3), math.radians(rng.uniform(28, 50)))
            Pb = grow(rng, p, bd, bl, 3, bend_axis=np.cross(bd, UP), bend=0.35, wobble=0.06)
            tube(g, Pb, np.linspace(0.0009, 0.0004, len(Pb)), rgba(FLEABANE['branch'] * shade, 0.2), sides=3)
            cb = Curve(Pb)
            nh = rng.randint(4, 9)
            for q in range(nh):
                ph, dh = cb.at(0.25 + 0.75 * q / max(nh - 1, 1))
                o = rot(dh, rng.uniform(0, 2 * math.pi)) @ perp(dh)
                if rng.random() < fluffy:
                    col = FLEABANE['fluff'] * rng.uniform(0.94, 1.03)
                    rh = rng.uniform(0.0022, 0.0032)
                else:
                    col = pick(rng, FLEABANE['heads']) * rng.uniform(0.92, 1.05)
                    rh = rng.uniform(0.0015, 0.0021)
                ellipsoid(g, ph + o * rh, np.diag([rh, rh, rh * 1.4]), col * 0.82, col, nu=5, nv=3, smooth=0.2)
            if rng.random() < 0.5:
                ph, dh = cb.at(0.1)
                blade(g, rng, ph, leaf_dir(dh, rng.uniform(0, 6.3), 0.5), rng.uniform(0.008, 0.016), 0.0012, (lc, lc),
                      n=3, droop=0.3, wfn=w_linear)
    return g


def spiny_leaf(g, rng, base, d, L, W, lobes, depth, cols, spine_c, droop=0.6, wave=0.35, spines=True, smooth=0.25,
               spine_len=(0.004, 0.008)):
    """Колючий лопастной лист чертополоха: край волнистый (доли то вверх, то вниз), на концах долей — колючки."""
    n = 4 * lobes + 3
    P, Ws, Ss = blade(g, rng, base, d, L, W, cols, n=n, droop=droop, wfn=w_lobed(lobes, depth), fold=0.2,
                      smooth=smooth, wave=wave, waves=lobes, roll=math.radians(rng.uniform(-60, 60)))
    if not spines:
        return
    T = tangents(P)
    for j in range(1, lobes + 1):
        i = min(n - 1, int(round(j / lobes * (n - 1) * 0.98)))
        for sign in (1, -1):
            q0 = P[i] + Ss[i] * sign * Ws[i]
            strand(g, q0, unit(Ss[i] * sign + T[i] * 0.7), rng.uniform(*spine_len), spine_c, w=0.0005)
    strand(g, P[-1], T[-1], rng.uniform(*spine_len), spine_c, w=0.0005)


def wings(g, rng, P, R, width, col, spine_c, spacing=0.018, spine_len=(0.006, 0.012), smooth=0.2):
    """Колючие крылья по стеблю (чертополох, татарник): две ленты по сторонам, волной, колючки по краю."""
    T = tangents(P)
    n = len(P)
    N1 = np.zeros_like(P)
    v = perp(T[0])
    for i in range(n):
        v = unit(v - T[i] * float(np.dot(v, T[i])))
        N1[i] = v
    crv = Curve(P)
    spiral = rng.uniform(1.0, 3.0)
    for sgn in (1, -1):
        S = np.array([rot(T[i], spiral * i / n) @ N1[i] * sgn for i in range(n)])
        wv = width * (0.6 + 0.4 * np.abs(np.sin(np.arange(n) * 1.7 + rng.random() * 3)))
        C = P + S * (np.asarray(R)[:, None] + wv[:, None] * 0.5)
        ribbon(g, C, wv * 0.5, S, rgba(col, smooth), 0.0)
        k = int(crv.L / spacing)
        for j in range(k):
            t = (j + rng.random()) / k
            i = min(n - 1, int(t * (n - 1)))
            q = P[i] + S[i] * (R[i] + wv[i])
            strand(g, q, unit(S[i] + T[i] * rng.uniform(0.2, 0.8)), rng.uniform(*spine_len), spine_c, w=0.0006)


def thistle_head(g, rng, c, d, size, kind, P_, spiny=False, fluff_p=0.6):
    """Корзинка: обёртка-яйцо (у татарника и чертополоха — с колючими листочками), сверху лиловая кисть цветков;
    kind: flower | fluff (белый пух) | dry (бурая, с остатком пуха с вероятностью fluff_p) | bud."""
    d = unit(d)
    e1 = perp(d)
    e2 = np.cross(d, e1)
    r = size * 0.5
    lo, hi = P_['invol_lo'], P_['invol_hi']
    if kind == 'dry':
        lo, hi = P_['dry'] * 0.8, P_['dry']
    ellipsoid(g, c, np.stack([e1 * r, e2 * r, d * size * 0.6], 1), lo, hi, nu=7, nv=4, smooth=0.2, rng=rng, bump=0.25)
    if spiny:
        for _ in range(rng.randint(10, 16)):
            a = rng.uniform(0, 2 * math.pi)
            o = math.cos(a) * e1 + math.sin(a) * e2
            q = c + o * r * 0.9 + d * rng.uniform(-0.2, 0.3) * size
            strand(g, q, unit(o - d * rng.uniform(0.0, 0.6)), size * rng.uniform(0.25, 0.5),
                   P_['spine'] if rng.random() < 0.5 else hi, w=0.0006)
    top = c + d * size * 0.5
    if kind == 'flower':
        col = jit(rng, pick(rng, P_['flower']), (0.9, 1.08), 0.03)
        ellipsoid(g, top, np.stack([e1 * r * 0.95, e2 * r * 0.95, d * size * 0.32], 1), col * 0.72, col, nu=7, nv=3,
                  smooth=0.15, rng=rng, bump=0.3)
        for _ in range(rng.randint(10, 18)):
            a = rng.uniform(0, 2 * math.pi)
            o = math.cos(a) * e1 + math.sin(a) * e2
            sp = rng.uniform(0.1, 0.9)
            strand(g, top + o * r * 0.5 * sp, unit(d + o * sp), size * rng.uniform(0.25, 0.45), col * 1.08, w=0.0006)
    elif kind == 'fluff':
        fluff(g, rng, top + d * size * 0.35, size * rng.uniform(0.9, 1.3), P_['fluff'] * rng.uniform(0.95, 1.02))
    elif kind == 'dry':
        if rng.random() < fluff_p:
            fluff(g, rng, top + d * size * 0.2, size * rng.uniform(0.35, 0.6), P_['fluff'] * 0.85, strands=10)


def cirsium(rng, H=1.0, fluffy=0.24, dry=0.26):
    """Бодяк полевой: тонкий стебель, ветвится в верхней трети; волнистые колючие ланцетные листья; корзинки
    1.5–2.5 см пучками на концах ветвей — к концу лета половина лиловые, остальные в белом пуху или сухие бурые."""
    g = Geo()
    lean = math.radians(rng.uniform(0, 8))
    az = rng.uniform(0, 2 * math.pi)
    out = np.array([math.cos(az), math.sin(az), 0.0])
    P = grow(rng, (0, 0, -0.02), unit(UP * math.cos(lean) + out * math.sin(lean)), H * 1.02, 10,
             bend_axis=np.cross(UP, out), bend=math.radians(rng.uniform(-3, 8)), wobble=0.03)
    shade = rng.uniform(0.92, 1.06)
    tube(g, P, np.linspace(0.0032, 0.0012, len(P)), stem_colours(len(P), CIRSIUM['stem_lo'], CIRSIUM['stem_hi'], shade),
         sides=5, phase=rng.random())
    crv = Curve(P)
    lc = pick(rng, CIRSIUM['leaf']) * rng.uniform(0.93, 1.07)
    n_leaves = rng.randint(18, 26)
    for i in range(n_leaves):
        t = 0.05 + 0.78 * (i + rng.random() * 0.5) / n_leaves
        p, d = crv.at(t)
        col = (CIRSIUM['under'] if rng.random() < 0.2 else lc) * rng.uniform(0.92, 1.06)
        if t < 0.2 and rng.random() < 0.4:
            col = DRIED_LEAF * rng.uniform(0.9, 1.1)
        L = rng.uniform(0.11, 0.2) * (1.2 - 0.55 * t) * min(1.2, H)
        spiny_leaf(g, rng, p, leaf_dir(d, i * 2.4 + rng.uniform(-0.3, 0.3), math.radians(rng.uniform(40, 70))), L,
                   L * rng.uniform(0.14, 0.2), rng.randint(4, 6), rng.uniform(0.35, 0.6), (col * 0.92, col),
                   CIRSIUM['spine'], droop=rng.uniform(0.4, 0.9), smooth=CIRSIUM['smooth'], spine_len=(0.003, 0.006))
    tips = []
    for j in range(rng.randint(3, 6)):
        t = 0.62 + 0.34 * (j + rng.random()) / 6
        p, d = crv.at(t)
        bd = leaf_dir(d, rng.uniform(0, 2 * math.pi), math.radians(rng.uniform(25, 45)))
        Pb = grow(rng, p, bd, rng.uniform(0.06, 0.18) * H, 4, bend_axis=np.cross(bd, UP), bend=0.4, wobble=0.05)
        tube(g, Pb, np.linspace(0.0016, 0.0009, len(Pb)), rgba(CIRSIUM['stem_hi'] * shade, 0.2), sides=4)
        tips.append((Pb[-1], unit(Pb[-1] - Pb[-2])))
        ph, dh = Curve(Pb).at(0.4)
        spiny_leaf(g, rng, ph, leaf_dir(dh, rng.uniform(0, 6.3), 0.8), rng.uniform(0.03, 0.06), 0.006, 3, 0.4, (lc, lc),
                   CIRSIUM['spine'], droop=0.4, spine_len=(0.002, 0.004))
    tips.append((P[-1], unit(P[-1] - P[-2])))
    for p, d in tips:
        for q in range(rng.randint(2, 4)):
            dd = leaf_dir(d, rng.uniform(0, 6.3), math.radians(rng.uniform(5, 35) if q else 0.0))
            ped = rng.uniform(0.005, 0.03)
            c = p + dd * ped
            tube(g, np.array([p, c]), [0.0009, 0.0008], rgba(CIRSIUM['stem_hi'] * shade, 0.2), sides=3)
            roll = rng.random()
            kind = 'fluff' if roll < fluffy else 'dry' if roll < fluffy + dry else 'bud' if roll > 0.93 else 'flower'
            thistle_head(g, rng, c + dd * 0.005, dd, rng.uniform(0.017, 0.025), kind, CIRSIUM)
    return g


def carduus(rng, H=1.2, fluffy=0.22, dry=0.28):
    """Чертополох (Carduus acanthoides, nutans): стебель с колючими крыльями, глубоко-лопастные колючие листья,
    поникающие корзинки 2.5–3.5 см с отогнутыми колючими листочками обёртки, пурпурные."""
    g = Geo()
    lean = math.radians(rng.uniform(0, 7))
    az = rng.uniform(0, 2 * math.pi)
    out = np.array([math.cos(az), math.sin(az), 0.0])
    P = grow(rng, (0, 0, -0.02), unit(UP * math.cos(lean) + out * math.sin(lean)), H * 1.02, 12,
             bend_axis=np.cross(UP, out), bend=math.radians(rng.uniform(-3, 8)), wobble=0.03)
    shade = rng.uniform(0.92, 1.06)
    R = np.linspace(0.005, 0.0015, len(P))
    tube(g, P, R, stem_colours(len(P), CARDUUS['stem_lo'], CARDUUS['stem_hi'], shade), sides=5, phase=rng.random())
    wings(g, rng, P, R, 0.008, CARDUUS['wing'] * shade, CARDUUS['spine'], spacing=0.02)
    crv = Curve(P)
    lc = pick(rng, CARDUUS['leaf']) * rng.uniform(0.93, 1.07)
    n_leaves = rng.randint(13, 19)
    for i in range(n_leaves):
        t = 0.04 + 0.7 * (i + rng.random() * 0.5) / n_leaves
        p, d = crv.at(t)
        col = (CARDUUS['under'] if rng.random() < 0.2 else lc) * rng.uniform(0.92, 1.06)
        L = rng.uniform(0.13, 0.27) * (1.2 - 0.7 * t) * min(1.2, H / 1.1)
        spiny_leaf(g, rng, p, leaf_dir(d, i * 2.4 + rng.uniform(-0.3, 0.3), math.radians(rng.uniform(45, 75))), L,
                   L * rng.uniform(0.16, 0.22), rng.randint(5, 7), rng.uniform(0.55, 0.75), (col * 0.92, col),
                   CARDUUS['spine'], droop=rng.uniform(0.5, 1.0), wave=0.5, smooth=CARDUUS['smooth'])
    tips = [(P[-1], unit(P[-1] - P[-2]))]
    for j in range(rng.randint(1, 4)):
        p, d = crv.at(0.6 + 0.35 * rng.random())
        bd = leaf_dir(d, rng.uniform(0, 6.3), math.radians(rng.uniform(25, 45)))
        Pb = grow(rng, p, bd, rng.uniform(0.1, 0.25) * H, 5, bend_axis=np.cross(bd, UP), bend=0.35, wobble=0.04)
        Rb = np.linspace(0.0025, 0.0012, len(Pb))
        tube(g, Pb, Rb, rgba(CARDUUS['stem_hi'] * shade, 0.2), sides=4)
        wings(g, rng, Pb, Rb, 0.005, CARDUUS['wing'] * shade, CARDUUS['spine'], spacing=0.025)
        tips.append((Pb[-1], unit(Pb[-1] - Pb[-2])))
    for p, d in tips:
        nod = unit(d + perp(d) * rng.uniform(0.4, 1.2) - UP * rng.uniform(0.3, 0.9))
        Pn = grow(rng, p, d, rng.uniform(0.03, 0.07), 3, bend_axis=np.cross(d, nod), bend=1.2)
        tube(g, Pn, [0.0012, 0.001, 0.0009, 0.0008], rgba(CARDUUS['stem_hi'] * shade, 0.2), sides=3)
        hd = unit(Pn[-1] - Pn[-2])
        roll = rng.random()
        kind = 'fluff' if roll < fluffy else 'dry' if roll < fluffy + dry else 'flower'
        thistle_head(g, rng, Pn[-1] + hd * 0.008, hd, rng.uniform(0.026, 0.036), kind, CARDUUS, spiny=True)
    return g


def onopordum(rng, H=1.5):
    """Татарник колючий: толстый серо-войлочный стебель с широкими колючими крыльями, огромные серебристые колючие
    листья (нижние дугой в стороны), канделябр ветвей с шаровидными корзинками 3–4.5 см: колючая обёртка и лиловая
    кисть."""
    g = Geo()
    lean = math.radians(rng.uniform(0, 5))
    az = rng.uniform(0, 2 * math.pi)
    out = np.array([math.cos(az), math.sin(az), 0.0])
    P = grow(rng, (0, 0, -0.02), unit(UP * math.cos(lean) + out * math.sin(lean)), H * 1.0, 12,
             bend_axis=np.cross(UP, out), bend=math.radians(rng.uniform(-2, 5)), wobble=0.02)
    shade = rng.uniform(0.94, 1.05)
    R = np.linspace(0.007, 0.0025, len(P))
    tube(g, P, R, stem_colours(len(P), ONOPORDUM['stem_lo'], ONOPORDUM['stem_hi'], shade), sides=6, phase=rng.random())
    wings(g, rng, P, R, 0.013, ONOPORDUM['wing'] * shade * 0.9, ONOPORDUM['spine'], spacing=0.022,
          spine_len=(0.008, 0.015))
    crv = Curve(P)
    lc = pick(rng, ONOPORDUM['leaf']) * rng.uniform(0.95, 1.05)
    n_leaves = rng.randint(13, 19)
    for i in range(n_leaves):
        t = 0.03 + 0.66 * (i + rng.random() * 0.5) / n_leaves
        p, d = crv.at(t)
        col = (ONOPORDUM['under'] if rng.random() < 0.25 else lc) * rng.uniform(0.93, 1.06)
        if t < 0.12 and rng.random() < 0.4:
            col = DRIED_LEAF * rng.uniform(0.95, 1.1)
        L = rng.uniform(0.2, 0.38) * (1.15 - 0.85 * t) * min(1.15, H / 1.4)
        spiny_leaf(g, rng, p, leaf_dir(d, i * 2.4 + rng.uniform(-0.3, 0.3), math.radians(rng.uniform(50, 80) if t < 0.2
                                                                                           else rng.uniform(35, 60))),
                   L, L * rng.uniform(0.17, 0.24), rng.randint(5, 7), rng.uniform(0.35, 0.5), (col * 0.93, col),
                   ONOPORDUM['spine'], droop=rng.uniform(0.6, 1.2), wave=0.45, smooth=ONOPORDUM['smooth'],
                   spine_len=(0.006, 0.012))
    tips = [(P[-1], unit(P[-1] - P[-2]))]
    for j in range(rng.randint(3, 6)):
        p, d = crv.at(0.5 + 0.42 * (j + rng.random()) / 6)
        bd = leaf_dir(d, j * 2.2 + rng.uniform(-0.4, 0.4), math.radians(rng.uniform(28, 45)))
        Pb = grow(rng, p, bd, rng.uniform(0.12, 0.3) * H, 6, bend_axis=np.cross(bd, UP), bend=0.55, wobble=0.03)
        Rb = np.linspace(0.004, 0.002, len(Pb))
        tube(g, Pb, Rb, rgba(ONOPORDUM['stem_hi'] * shade, 0.2), sides=4)
        wings(g, rng, Pb, Rb, 0.012, ONOPORDUM['wing'] * shade, ONOPORDUM['spine'], spacing=0.025)
        tips.append((Pb[-1], unit(Pb[-1] - Pb[-2])))
        ph, dh = Curve(Pb).at(0.3)
        spiny_leaf(g, rng, ph, leaf_dir(dh, rng.uniform(0, 6.3), 0.9), rng.uniform(0.05, 0.1), 0.012, 4, 0.4, (lc, lc),
                   ONOPORDUM['spine'], droop=0.4)
    for p, d in tips:
        roll = rng.random()
        # к концу лета у татарника половина корзинок отцвела: бурые, часть в пуху
        kind = 'dry' if roll < 0.36 else 'fluff' if roll < 0.5 else 'bud' if roll < 0.56 else 'flower'
        thistle_head(g, rng, p + d * 0.012, d, rng.uniform(0.03, 0.045), kind, ONOPORDUM, spiny=True)
    return g


def dead_stalk(rng, H=1.1, kind='ragweed', broken=0.25, bent=0.15):
    """Прошлогоднее будылье: голый серо-бурый стебель того же склада, что живой (ветви амброзии с сухими кистями,
    метёлка мелколепестника с остатками пуха, метёлки полыни, чертополох с бурыми корзинками), 25 % сломаны (верх висит
    или лежит), 15 % согнуты."""
    g = Geo()
    up_part = Geo()
    sc = pick(rng, DEAD['stalk']) * rng.uniform(0.92, 1.06)
    lean = math.radians(rng.uniform(25, 60) if rng.random() < bent else rng.uniform(0, 10))
    az = rng.uniform(0, 2 * math.pi)
    out = np.array([math.cos(az), math.sin(az), 0.0])
    P = grow(rng, (0, 0, -0.02), unit(UP * math.cos(lean) + out * math.sin(lean)), H * 1.02, 12,
             bend_axis=np.cross(UP, out), bend=math.radians(rng.uniform(-6, 14)), wobble=0.04)
    tb = rng.uniform(0.35, 0.75) if rng.random() < broken else None
    r0 = {'ragweed': 0.0055, 'fleabane': 0.0038, 'wormwood': 0.0032, 'thistle': 0.0065}[kind] * rng.uniform(0.85, 1.2)
    R = np.linspace(r0, 0.001, len(P))
    C = stem_colours(len(P), DEAD['base'], sc, 1.0, smooth=0.15, reach=3.0)
    crv = Curve(P)
    if tb is None:
        tube(g, P, R, C, sides=5, phase=rng.random())
    else:
        k = max(1, min(len(P) - 2, int(round(tb * (len(P) - 1)))))
        tube(g, P[:k + 1], R[:k + 1], C[:k + 1], sides=5)
        tube(up_part, P[k:], R[k:], C[k:], sides=5)
        tb = k / (len(P) - 1)

    def target(t):
        return up_part if (tb is not None and t > tb) else g

    if kind == 'ragweed':
        # ветвистее живой (8–14 ветвей, у каждой 1–3 побега): срез будыльев должен закрывать хотя бы пятую часть
        n_br = rng.randint(8, 14)
        for j in range(n_br):
            t = 0.22 + 0.68 * (j + rng.random()) / n_br
            p, d = crv.at(t)
            bd = leaf_dir(d, j * 2.4 + rng.uniform(-0.4, 0.4), math.radians(rng.uniform(30, 55)))
            Pb = grow(rng, p, bd, H * rng.uniform(0.14, 0.3) * (1.2 - 0.6 * t), 5, bend_axis=np.cross(bd, UP),
                      bend=0.35, wobble=0.07)
            tube(target(t), Pb, np.linspace(0.0022, 0.0009, len(Pb)), rgba(sc * 1.03, 0.15), sides=4)
            ends = [(Pb[-1], unit(Pb[-1] - Pb[-2]))]
            for q in range(rng.randint(1, 3)):
                ph, dh = Curve(Pb).at(rng.uniform(0.4, 0.8))
                Ps = grow(rng, ph, leaf_dir(dh, rng.uniform(0, 6.3), math.radians(35)), rng.uniform(0.05, 0.12), 3,
                          wobble=0.08)
                tube(target(t), Ps, np.linspace(0.0011, 0.0006, len(Ps)), rgba(sc, 0.15), sides=3)
                ends.append((Ps[-1], unit(Ps[-1] - Ps[-2])))
            for pe, de in ends:
                raceme(target(t), rng, pe, de, rng.uniform(0.03, 0.09), sc, DEAD['heads'], smooth=0.12,
                       spacing=0.0065, head_r=(0.0019, 0.0027))
        for q in range(rng.randint(3, 5)):
            raceme(target(1.0), rng, P[-1], leaf_dir(unit(P[-1] - P[-2]), rng.uniform(0, 6.3), math.radians(14 * q)),
                   rng.uniform(0.05, 0.11), sc, DEAD['heads'], smooth=0.12, spacing=0.0065, head_r=(0.0019, 0.0027))
    elif kind == 'fleabane':
        ts = rng.uniform(0.58, 0.7)
        n_br = int(crv.L * (1 - ts) / 0.013)
        for j in range(n_br):
            t = ts + (1 - ts) * (j + rng.random() * 0.7) / n_br
            p, d = crv.at(t)
            u = (t - ts) / (1 - ts)
            Pb = grow(rng, p, leaf_dir(d, j * 2.4, math.radians(rng.uniform(30, 50))),
                      0.025 + 0.09 * (1 - u) * rng.uniform(0.6, 1.0), 3, wobble=0.08)
            tube(target(t), Pb, np.linspace(0.0009, 0.0005, len(Pb)), rgba(sc * 1.05, 0.15), sides=3)
            for q in range(rng.randint(2, 5)):
                ph, _ = Curve(Pb).at(rng.uniform(0.3, 1.0))
                col = DEAD['fluff'] * rng.uniform(0.85, 1.0) if rng.random() < 0.22 else pick(rng, DEAD['heads'])
                rh = rng.uniform(0.0018, 0.003)
                ellipsoid(target(t), ph, np.diag([rh, rh, rh * 1.2]), col * 0.85, col, nu=5, nv=3, smooth=0.1)
    elif kind == 'wormwood':
        for j in range(rng.randint(8, 13)):
            t = 0.4 + 0.57 * (j + rng.random()) / 13
            p, d = crv.at(t)
            Pb = grow(rng, p, leaf_dir(d, j * 2.4 + rng.uniform(-0.4, 0.4), math.radians(rng.uniform(20, 40))),
                      rng.uniform(0.05, 0.14) * (1.35 - t), 4, bend_axis=None, gravity=0.8, wobble=0.07)
            tube(target(t), Pb, np.linspace(0.0011, 0.0005, len(Pb)), rgba(sc, 0.15), sides=3)
            cb = Curve(Pb)
            for q in range(rng.randint(6, 11)):
                ph, dh = cb.at(rng.uniform(0.1, 1.0))
                col = pick(rng, DEAD['heads']) * rng.uniform(0.9, 1.1)
                rh = rng.uniform(0.0016, 0.0024)
                ellipsoid(target(t), ph - UP * rh, np.diag([rh, rh, rh]), col * 0.85, col, nu=5, nv=3, smooth=0.1)
    elif kind == 'thistle':
        wings(g, rng, P[:max(3, len(P) // 2)], R[:max(3, len(P) // 2)], 0.004, sc * 0.92, sc * 1.1, spacing=0.05)
        for j in range(rng.randint(3, 6)):
            t = 0.5 + 0.45 * rng.random()
            p, d = crv.at(t)
            bd = leaf_dir(d, rng.uniform(0, 6.3), math.radians(rng.uniform(25, 45)))
            Pb = grow(rng, p, bd, rng.uniform(0.08, 0.2) * H, 4, bend_axis=np.cross(bd, UP), bend=0.3, wobble=0.05)
            tube(target(t), Pb, np.linspace(0.002, 0.001, len(Pb)), rgba(sc, 0.15), sides=4)
            thistle_head(target(t), rng, Pb[-1], unit(Pb[-1] - Pb[-2]), rng.uniform(0.014, 0.026), 'dry',
                         dict(CIRSIUM, dry=pick(rng, DEAD['heads']) * 1.05, fluff=DEAD['fluff']), fluff_p=0.3)
        thistle_head(target(1.0), rng, P[-1], unit(P[-1] - P[-2]), rng.uniform(0.016, 0.028), 'dry',
                     dict(CIRSIUM, dry=pick(rng, DEAD['heads']) * 1.05, fluff=DEAD['fluff']), fluff_p=0.3)
    for _ in range(rng.randint(0, 3)):
        # скрученные остатки листьев у узлов
        t = rng.uniform(0.1, 0.6)
        p, d = crv.at(t)
        blade(target(t), rng, p, leaf_dir(d, rng.uniform(0, 6.3), 1.0), rng.uniform(0.02, 0.05), 0.003,
              (DEAD['leaf'], DEAD['leaf'] * 0.9), n=4, droop=1.6, twist=2.0, smooth=0.1)
    if tb is not None:
        pivot, dpiv = crv.at(tb)
        axis = np.cross(dpiv, UP)
        if np.linalg.norm(axis) < 1e-6:
            axis = perp(dpiv)
        ang = rng.uniform(1.8, 2.8) if rng.random() < 0.6 else rng.uniform(0.9, 1.6)
        M = rot(rot(UP, rng.uniform(-0.8, 0.8)) @ axis, -ang)

        def fold(V):
            W = (V - pivot) @ M.T + pivot
            W[:, 2] = np.maximum(W[:, 2], 0.004)   # что ушло бы под землю, лежит на ней
            return W
        up_part.transform(fold)
        g.add_geo(up_part)
    return g


def dead_bush(rng, H=1.0, stems=(5, 11)):
    """Прошлогодний куст полыни: 5–11 сухих стеблей с метёлками из одного места, наружные клонятся."""
    g = Geo()
    for k in range(rng.randint(*stems)):
        st = dead_stalk(rng, H * rng.uniform(0.75, 1.05), kind='wormwood', broken=0.15, bent=0.0)
        a = rng.uniform(0, 2 * math.pi)
        lean = math.radians(rng.uniform(4, 24))
        M = rot(np.array([-math.sin(a), math.cos(a), 0.0]), lean)
        g.add_geo(st, (rng.uniform(-0.04, 0.04), rng.uniform(-0.04, 0.04), 0.0), M)
    return g


def cynodon(rng, H=0.3):
    """Свинорой (Cynodon dactylon): стелющиеся побеги с короткими серо-зелёными листьями и соломины с 3–6 пальчатыми
    колосками на верхушке."""
    g = Geo()
    sm = LOW['smooth']
    for _ in range(rng.randint(3, 6)):
        az = rng.uniform(0, 6.3)
        d = np.array([math.cos(az), math.sin(az), 0.08])
        Ps = grow(rng, (0, 0, 0.004), d, rng.uniform(0.15, 0.35), 5, gravity=0.3, wobble=0.15)
        Ps[:, 2] = np.maximum(Ps[:, 2], 0.004)
        tube(g, Ps, 0.0012, rgba(LOW['dry'][3] * 0.9, 0.2), sides=3)
        for p in Ps[1:]:
            for q in range(rng.randint(2, 4)):
                col = pick(rng, LOW['grass']) * rng.uniform(0.9, 1.08)
                blade(g, rng, p, leaf_dir(UP, rng.uniform(0, 6.3), math.radians(rng.uniform(20, 60))),
                      rng.uniform(0.025, 0.07), rng.uniform(0.0012, 0.002), (col * 0.9, col), n=4, droop=0.6,
                      wfn=w_linear, smooth=sm)
    for _ in range(rng.randint(2, 6)):
        h = H * rng.uniform(0.6, 1.0)
        b = np.array([rng.uniform(-0.08, 0.08), rng.uniform(-0.08, 0.08), 0.0])
        Pc = grow(rng, b, leaf_dir(UP, rng.uniform(0, 6.3), math.radians(rng.uniform(0, 20))), h, 5, wobble=0.06)
        tube(g, Pc, np.linspace(0.0011, 0.0007, len(Pc)), rgba(pick(rng, LOW['dry']), 0.2), sides=3)
        top = Pc[-1]
        sc = pick(rng, LOW['cyn_spike'])
        for f in range(rng.randint(3, 6)):
            dd = leaf_dir(UP, f * 1.3 + rng.uniform(-0.3, 0.3), math.radians(rng.uniform(25, 60)))
            Pf = grow(rng, top, dd, rng.uniform(0.025, 0.05), 3, gravity=0.6)
            ribbon(g, Pf, np.full(len(Pf), 0.0011), np.array([horiz_side(t) for t in tangents(Pf)]),
                   rgba(sc * rng.uniform(0.9, 1.1), 0.2))
    return g


def plantain(rng, H=0.4):
    """Подорожник ланцетный: розетка ланцетных листьев дугой и цветоносы с бурым колосом и светлым кольцом тычинок."""
    g = Geo()
    lc = pick(rng, LOW['plantain'])
    for i in range(rng.randint(4, 7)):
        col = lc * rng.uniform(0.88, 1.1)
        blade(g, rng, (0, 0, 0.003), leaf_dir(UP, i * 2.4 + rng.uniform(-0.2, 0.2), math.radians(rng.uniform(35, 70))),
              rng.uniform(0.06, 0.14), rng.uniform(0.005, 0.009), (col * 0.85, col), n=5, droop=rng.uniform(0.5, 1.1),
              wfn=w_lanceolate, smooth=0.3)
    for _ in range(rng.randint(2, 6)):
        h = H * rng.uniform(0.6, 1.0)
        Pc = grow(rng, (rng.uniform(-0.02, 0.02), rng.uniform(-0.02, 0.02), 0), leaf_dir(UP, rng.uniform(0, 6.3),
                                                                                         math.radians(rng.uniform(5, 30))),
                  h, 5, bend_axis=None, wobble=0.05)
        tube(g, Pc, np.linspace(0.0012, 0.0009, len(Pc)), rgba(lc * 1.1, 0.2), sides=3)
        d = unit(Pc[-1] - Pc[-2])
        L = rng.uniform(0.015, 0.035)
        ellipsoid(g, Pc[-1] + d * L * 0.5, sphere_axes(0.0045, L * 0.5, d), LOW['spike'] * 0.85, LOW['spike'], nu=6,
                  nv=4, smooth=0.15)
        if rng.random() < 0.7:
            c = Pc[-1] + d * L * rng.uniform(0.2, 0.6)
            ellipsoid(g, c, sphere_axes(0.0062, 0.003, d), LOW['stamen'] * 0.9, LOW['stamen'], nu=7, nv=3, smooth=0.1)
    return g


def dry_grass(rng, H=0.5):
    """Сухая трава (костёр, пырей): пучок сухих листьев дугой и соломины с поникающими метёлками."""
    g = Geo()
    for _ in range(rng.randint(18, 40)):
        col = (pick(rng, LOW['dry']) if rng.random() < 0.7 else pick(rng, LOW['grass'])) * rng.uniform(0.9, 1.08)
        blade(g, rng, (rng.uniform(-0.03, 0.03), rng.uniform(-0.03, 0.03), 0),
              leaf_dir(UP, rng.uniform(0, 6.3), math.radians(rng.uniform(10, 45))), rng.uniform(0.1, 0.3),
              rng.uniform(0.0015, 0.0028), (col * 0.85, col), n=4, droop=rng.uniform(0.4, 1.4), wfn=w_linear, smooth=0.2)
    for _ in range(rng.randint(3, 8)):
        h = H * rng.uniform(0.6, 1.05)
        Pc = grow(rng, (rng.uniform(-0.03, 0.03), rng.uniform(-0.03, 0.03), 0),
                  leaf_dir(UP, rng.uniform(0, 6.3), math.radians(rng.uniform(3, 22))), h, 6, wobble=0.05)
        col = pick(rng, LOW['dry'])
        tube(g, Pc, np.linspace(0.0011, 0.0007, len(Pc)), rgba(col, 0.2), sides=3)
        top = Pc[-1]
        for s in range(rng.randint(4, 9)):
            dd = leaf_dir(UP, s * 2.0 + rng.uniform(-0.4, 0.4), math.radians(rng.uniform(40, 110)))
            Ps = grow(rng, top - UP * rng.uniform(0, 0.06), dd, rng.uniform(0.02, 0.06), 3, gravity=1.5)
            tube(g, Ps, 0.0004, rgba(col, 0.2), sides=3)
            ellipsoid(g, Ps[-1], sphere_axes(0.0018, 0.008, unit(Ps[-1] - Ps[-2])), col * 0.9, col * 1.05, nu=4, nv=3,
                      smooth=0.2)
    return g


def low_herb(rng, H=0.25):
    """Розетка (цикорий, одуванчик, спорыш): лопастные листья у земли и пара стеблей с мелкими листьями."""
    g = Geo()
    lc = pick(rng, LOW['herb'])
    for i in range(rng.randint(5, 10)):
        col = lc * rng.uniform(0.88, 1.1)
        blade(g, rng, (0, 0, 0.003), leaf_dir(UP, i * 2.4, math.radians(rng.uniform(55, 80))), rng.uniform(0.06, 0.14),
              rng.uniform(0.008, 0.016), (col * 0.85, col), n=13, droop=rng.uniform(0.2, 0.6), wfn=w_lobed(3, 0.5),
              smooth=0.3)
    for _ in range(rng.randint(0, 3)):
        h = H * rng.uniform(0.5, 1.0)
        Pc = grow(rng, (0, 0, 0), leaf_dir(UP, rng.uniform(0, 6.3), math.radians(rng.uniform(5, 30))), h, 4, wobble=0.1)
        tube(g, Pc, 0.0012, rgba(lc, 0.2), sides=3)
        crv = Curve(Pc)
        for q in range(rng.randint(3, 6)):
            p, d = crv.at(rng.uniform(0.2, 0.95))
            blade(g, rng, p, leaf_dir(d, rng.uniform(0, 6.3), 0.9), rng.uniform(0.01, 0.03), 0.003, (lc, lc), n=3,
                  droop=0.3)
    return g


PLANTS = dict(wormwood=wormwood_tuft, ragweed=ragweed, fleabane=fleabane, cirsium=cirsium, carduus=carduus,
              onopordum=onopordum, dead=dead_stalk, dead_bush=dead_bush, cynodon=cynodon, plantain=plantain,
              dry_grass=dry_grass, low_herb=low_herb)


# ============================================================================================== ассеты: области атласа
def mix(*items):
    """Смесь растений области: (вес, растение, {параметр: значение или (мин, макс)})."""
    return list(items)


def strip(name, y, texel, seg, depth, density, plants, rows=512, pad=24, width=2048, role='mid', pool=16):
    """Полоса «вид сбоку»; role — где в пятне её участки: tall — середина, mid — везде, low — край (пятно — бугор,
    а не цилиндр с плоским верхом); pool — неповторимых растений (у тяжёлой полыни меньше: память видеокарты)."""
    return dict(name=name, kind='strip', x=0, y=y, w=width, h=rows, pad=pad, texel=texel, seg=seg, depth=depth,
                density=density, mix=plants, role=role, pool=pool)


TILE_TEXEL = 0.0031    # плитка 512 px = 1.59 м: шатёр r 0.79 м, непрозрачен до ~0.65 м


def tile(name, x, y, count, z_cut, plants, size=512, texel=TILE_TEXEL, pool=10, cover=0.9):
    """Плитка «вид сверху» для шатра: шатёр — восьмиугольник с углами чуть внутри плитки (dome_r), содержимое — внутри
    его апофемы (spread); count — растений для начала, дальше досыпаются, пока круг не закрыт на cover."""
    dome_r = size * texel * 0.5 - 0.004
    return dict(name=name, kind='tile', x=x, y=y, w=size, h=size, pad=0, texel=texel, count=count, z_cut=z_cut,
                mix=plants, dome_r=round(dome_r, 4), spread=round(dome_r * math.cos(math.pi / 8) - 0.012, 4),
                pool=pool, cover=cover)


def tiles(prefix, y, specs, **kw):
    """Ряд из четырёх плиток 512²; specs — (растений для начала, z_cut, смесь) на каждую."""
    return [tile(f'{prefix}_t{i + 1}', i * 512, y, n, zc, plants, **kw) for i, (n, zc, plants) in enumerate(specs)]


def variant(suffix, seed, scale, spread, cards, core=3, tents=2, offset=(0.3, 0.4), tent_h=(0.2, 0.3), **kw):
    """Вариант пятна (свой FBX и папка <имя><suffix>): seed — свой план карточек; scale — размер карточек и шатров;
    spread — круг карточек, м; cards — карточек, из них core крест-накрест в середине; tents — шатров, offset — их
    относ от середины (м), tent_h — вершина в долях роста. Ещё: ring_roles — какие полосы берут середина, кольцо и
    край; prefer — вид растений для первой карточки середины (prefer_core — для всех карточек середины); lean — наклон
    краевых наружу, градусы; rim — высота края шатра, м; tent_profile — cone | dome."""
    return dict(suffix=suffix, seed=seed, scale=scale, spread=spread, cards=cards, core=core, tents=tents,
                tent_offset=offset, tent_height=tent_h, **kw)


# тёмная полынь обыкновенная — только в высокой полосе (её берут середины пятен, у варианта _c — нарочно): шпили по
# всем полосам ставили над серебристыми буграми тёмные столбы, и с 15 м куртины снова читались колючими кристаллами
WW_TALL = mix((0.72, 'wormwood', dict(H=(1.05, 1.32), width=(0.55, 0.8), late=(0.0, 0.25))),
              (0.28, 'wormwood', dict(H=(1.4, 1.7), width=(0.6, 0.9), vulgaris=True, late=(0.0, 0.3))))
WW_MID = mix((1.0, 'wormwood', dict(H=(0.8, 1.18), width=(0.5, 0.8), late=(0.1, 0.4), dead=(0, 3))))
WW_LOW = mix((1.0, 'wormwood', dict(H=(0.45, 0.8), width=(0.45, 0.7), late=(0.0, 0.2))))
WW_TOP = mix((0.85, 'wormwood', dict(H=(0.9, 1.25), width=(0.5, 0.75), late=(0.0, 0.25))),
             (0.15, 'wormwood', dict(H=(1.2, 1.55), width=(0.5, 0.7), vulgaris=True, late=(0.0, 0.3))))
RG_TALL = mix((0.6, 'ragweed', dict(H=(1.2, 1.85))), (0.4, 'fleabane', dict(H=(1.15, 1.85))))
RG_MID = mix((0.55, 'ragweed', dict(H=(0.85, 1.4))), (0.35, 'fleabane', dict(H=(0.8, 1.45))),
             (0.1, 'ragweed', dict(H=(0.4, 0.7))))
RG_LOW = mix((0.45, 'ragweed', dict(H=(0.4, 0.95))), (0.3, 'fleabane', dict(H=(0.5, 1.02))),
             (0.25, 'dead', dict(H=(0.6, 1.02), kind='ragweed')))
TH_A = mix((0.45, 'onopordum', dict(H=(1.6, 2.1))), (0.55, 'cirsium', dict(H=(0.8, 1.25))))
TH_B = mix((0.35, 'carduus', dict(H=(0.9, 1.5))), (0.65, 'cirsium', dict(H=(0.65, 1.15))))
TH_C = mix((1.0, 'cirsium', dict(H=(0.5, 0.95), fluffy=(0.2, 0.35), dry=(0.2, 0.3))))
TH_TOP = mix((0.25, 'onopordum', dict(H=(1.5, 2.0))), (0.25, 'carduus', dict(H=(0.9, 1.4))),
             (0.5, 'cirsium', dict(H=(0.7, 1.15))))
DS_A = mix((0.55, 'dead', dict(H=(0.8, 1.5), kind='ragweed')), (0.45, 'dead', dict(H=(0.9, 1.5), kind='fleabane')))
DS_B = mix((0.35, 'dead_bush', dict(H=(0.7, 1.25))), (0.35, 'dead', dict(H=(0.75, 1.38), kind='thistle')),
           (0.3, 'dead', dict(H=(0.8, 1.38), kind='ragweed')))
DS_C = mix((0.3, 'dead', dict(H=(0.6, 0.98), kind='ragweed', broken=0.4)),
           (0.25, 'dead', dict(H=(0.6, 0.98), kind='fleabane', broken=0.35)),
           (0.25, 'dead_bush', dict(H=(0.55, 0.9))), (0.2, 'dead', dict(H=(0.6, 0.98), kind='thistle', broken=0.35)))
LOW_MIX = mix((0.35, 'cynodon', dict(H=(0.2, 0.38))), (0.12, 'plantain', dict(H=(0.3, 0.5))),
              (0.4, 'dry_grass', dict(H=(0.35, 0.58))), (0.13, 'low_herb', dict(H=(0.15, 0.3))))

# Подсказки профиля (hint): density — экз/м² на вид (делить поровну на варианты); clump — ClumpScale (м) и ClumpStrength;
# land — LandScales сверх Fallow 1 (остальные группы 0: бурьян — на брошенной земле); seasons — весна, лето, осень, зима
# (весна в игре — середина апреля: амброзия и мелколепестник — всходы, бодяк — розетки, полынь отрастает до 0.4 м, а
# прошлогодние будылья видны лучше всего).
ASSETS = {
    'ruderal_wormwood_stand': dict(
        material='ruderal_wormwood', size=(2048, 2048),
        regions=[strip('ww_a', 0, 0.0029, (0.55, 1.05), 0.5, 14.0, WW_TALL, rows=640, role='tall', pool=10),
                 strip('ww_b', 640, 0.0026, (0.5, 1.0), 0.5, 16.0, WW_MID, pool=10),
                 strip('ww_c', 1152, 0.0024, (0.45, 0.9), 0.5, 20.0, WW_LOW, rows=384, role='low', pool=10),
                 *tiles('ww', 1536, [(10, 0.3, WW_TOP), (10, 0.3, WW_TOP), (10, 0.35, WW_TOP), (10, 0.25, WW_TOP)],
                        pool=7)],
        # шалфейная серо-зелень (было почти нейтрально-серое 0.13/0.146/0.114 — рядом с амброзией читалось камнем)
        target=(0.115, 0.135, 0.095), saturation=0.85, shade=dict(base=0.55, height=0.6, ao=0.72),
        cards=dict(length=(0.5, 1.0), min_gap=0.2, kink=(8, 20), height_scale=(0.92, 1.06), skirt=0.08, seg_split=0.85,
                   taper=0.62, lean=(0, 6)),
        # полынь — низким широким бугром; второй — обычный; третий — с тёмными «шпилями» полыни обыкновенной
        variants=[variant('', 'a', 0.92, 1.05, 12, core=3, tents=3, offset=(0.34, 0.44), tent_h=(0.14, 0.2),
                          ring_roles=(('mid', 'tall'), ('mid', 'low'), ('low',))),
                  variant('_b', 'b', 1.0, 1.0, 12, core=3, tents=3, offset=(0.32, 0.42), tent_h=(0.14, 0.2)),
                  variant('_c', 'c', 1.04, 0.95, 11, core=3, tents=3, offset=(0.3, 0.4), tent_h=(0.13, 0.19),
                          ring_roles=(('tall',), ('tall', 'mid'), ('mid', 'low')), prefer='mugwort')],
        wind=0.55,
        material_props=dict(alphaCutoff=0.35, stiffness=0.85, drag=0.45, shiverDrag=0.05, shiverDirectionality=0.6,
                            aoMin=0.45, smoothnessMax=0.22, specular=0.15, normalScale=0.6, dryColor=[0.93, 0.93, 0.9],
                            colorNoiseSpread=0.35, puddleSink=0.25, sizeVariation=0.3, scaleFade=[100000, 40],
                            seasonKeep=1.0),
        nr=dict(renderDistance=100.0, shadows=False, densityInDistance=0.6), cast_shadows=False,
        hint=dict(density=(0.25, 0.6), clump=(12.0, 0.6), land={}, seasons=(0.3, 1.0, 1.0, 0.0), aboveSnow=False,
                  note='years 3-5+ after abandonment: sage-grey rounded clumps on older fallows and field edges, '
                       'in patches (ClumpScale 12 m) among the ragweed, not an even salt-and-pepper mix')),
    'ruderal_ragweed_stand': dict(
        material='ruderal_ragweed', size=(2048, 2048),
        regions=[strip('rg_a', 0, 0.0032, (0.55, 1.1), 0.45, 44.0, RG_TALL, rows=640, role='tall'),
                 strip('rg_b', 640, 0.0031, (0.5, 1.0), 0.45, 46.0, RG_MID),
                 strip('rg_c', 1152, 0.003, (0.45, 0.9), 0.45, 50.0, RG_LOW, rows=384, role='low'),
                 *tiles('rg', 1536, [(60, 0.25, RG_TALL), (60, 0.25, RG_MID), (60, 0.2, RG_MID), (60, 0.25, RG_TALL)])],
        target=(0.08, 0.108, 0.052), saturation=0.85, shade=dict(base=0.5, height=0.6, ao=0.7),
        cards=dict(length=(0.5, 1.05), min_gap=0.2, kink=(6, 16), height_scale=(0.9, 1.06), skirt=0.08, seg_split=0.8,
                   taper=0.55, lean=(1, 8)),
        # обычная заросль; высокий рваный верх с метёлками мелколепестника; низкая молодая
        variants=[variant('', 'a', 1.0, 1.05, 13, core=3, tents=4, offset=(0.34, 0.44), tent_h=(0.12, 0.18)),
                  variant('_b', 'b', 1.0, 1.05, 12, core=4, tents=4, offset=(0.34, 0.44), tent_h=(0.11, 0.17),
                          ring_roles=(('tall',), ('tall', 'mid'), ('mid', 'low')), prefer='fleabane'),
                  variant('_c', 'c', 0.9, 1.0, 14, core=2, tents=4, offset=(0.32, 0.42), tent_h=(0.12, 0.18),
                          ring_roles=(('mid', 'tall'), ('mid', 'low'), ('low',)))],
        wind=0.7,
        material_props=dict(alphaCutoff=0.35, stiffness=0.75, drag=0.6, shiverDrag=0.06, shiverDirectionality=0.6,
                            aoMin=0.45, smoothnessMax=0.22, specular=0.15, normalScale=0.6, dryColor=[0.94, 0.93, 0.86],
                            colorNoiseSpread=0.35, puddleSink=0.3, sizeVariation=0.45, scaleFade=[100000, 40],
                            seasonKeep=1.0),
        nr=dict(renderDistance=120.0, shadows=False, densityInDistance=0.6), cast_shadows=False,
        hint=dict(density=(0.5, 1.0), clump=(15.0, 0.3), land=dict(FieldEdge=0.1, Track=0.15),
                  seasons=(0.1, 1.0, 0.7, 0.0), aboveSnow=False,
                  note='years 1-3: the main cover of a fallow field; alone it closes (<=15-20% bare from 30 m) at '
                       '~1.0/m2, with the low mat, dead stems and wormwood at ~0.6-0.7/m2; '
                       'ruderal_dead_stems is its base layer; FieldEdge/Track 0.1-0.15 = a few roadside weeds elsewhere')),
    'ruderal_thistle_clump': dict(
        material='ruderal_thistle', size=(2048, 2048),
        regions=[strip('th_a', 0, 0.0036, (0.6, 1.2), 0.5, 16.0, TH_A, rows=640, role='tall'),
                 strip('th_b', 640, 0.0034, (0.55, 1.05), 0.5, 22.0, TH_B),
                 strip('th_c', 1152, 0.003, (0.45, 0.9), 0.45, 42.0, TH_C, rows=384, role='low'),
                 *tiles('th', 1536, [(30, 0.25, TH_TOP), (30, 0.25, TH_B), (36, 0.2, TH_C), (30, 0.25, TH_A)])],
        target=(0.088, 0.102, 0.078), saturation=0.85, shade=dict(base=0.55, height=0.55, ao=0.72),
        # верх полос светлят белый пух и лиловые корзинки: плитки с 0.86 читались сбоку бело-серыми ковриками
        tile_grade=0.74,
        cards=dict(length=(0.55, 1.15), min_gap=0.22, kink=(8, 22), height_scale=(0.9, 1.06), skirt=0.08,
                   seg_split=0.85, taper=0.6, lean=(0, 6)),
        # бодяк с одним канделябром татарника; чертополох; редкие высокие канделябры татарника с прогалами
        variants=[variant('', 'a', 1.0, 0.95, 11, core=3, tents=2, offset=(0.28, 0.38), tent_h=(0.1, 0.15), prefer='onopordum'),
                  variant('_b', 'b', 0.9, 0.92, 11, core=3, tents=2, offset=(0.28, 0.38), tent_h=(0.11, 0.16), prefer='carduus',
                          ring_roles=(('mid', 'tall'), ('mid', 'low'), ('low', 'mid'))),
                  variant('_c', 'c', 1.0, 1.0, 8, core=2, tents=2, offset=(0.3, 0.4), tent_h=(0.1, 0.14),
                          prefer='onopordum', prefer_core=True, ring_roles=(('tall',), ('mid', 'low'), ('low',)))],
        wind=0.5,
        material_props=dict(alphaCutoff=0.38, stiffness=0.9, drag=0.4, shiverDrag=0.04, shiverDirectionality=0.6,
                            aoMin=0.45, smoothnessMax=0.2, specular=0.15, normalScale=0.6, dryColor=[0.94, 0.92, 0.88],
                            colorNoiseSpread=0.35, puddleSink=0.25, sizeVariation=0.3, scaleFade=[100000, 40],
                            seasonKeep=0.0),
        nr=dict(renderDistance=100.0, shadows=False, densityInDistance=0.6), cast_shadows=False,
        hint=dict(density=(0.06, 0.25), clump=(5.0, 0.7), land={}, seasons=(0.15, 1.0, 0.8, 0.0), aboveSnow=False,
                  note='clumps inside ragweed stands, along tracks and crater rims; seasonKeep 0: dries brown in autumn')),
    'ruderal_dead_stems': dict(
        material='ruderal_dead_stems', size=(2048, 2048),
        regions=[strip('ds_a', 0, 0.0026, (0.5, 1.0), 0.5, 52.0, DS_A, rows=640, role='tall'),
                 strip('ds_b', 640, 0.0029, (0.5, 1.0), 0.5, 38.0, DS_B),
                 strip('ds_c', 1152, 0.0028, (0.45, 0.9), 0.5, 44.0, DS_C, rows=384, role='low'),
                 *tiles('ds', 1536, [(20, 0.3, DS_A), (20, 0.3, DS_B), (20, 0.25, DS_C), (20, 0.3, DS_A)], cover=0.42)],
        # тёплый серо-бурый (r/b ≈ 1.45; было 1.29 — серый, сиреневый рядом с зеленью); низ не темнее 0.8 (чёрточка у снега)
        target=(0.15, 0.135, 0.103), saturation=0.75, shade=dict(base=0.8, height=0.5, ao=0.85),
        # сверху — редкая щетина будыльев, сквозь неё снег и земля (не бурые диски): плитка редкая (cover 0.42) и не
        # светлее срезов
        tile_grade=0.8,
        cards=dict(length=(0.5, 1.0), min_gap=0.2, kink=(6, 18), height_scale=(0.9, 1.06), skirt=0.08, seg_split=0.8,
                   taper=0.6, lean=(0, 5)),
        # редкие прямые с обломанными верхами; полёгшие пониже и гуще; высокие
        variants=[variant('', 'a', 1.0, 0.95, 12, core=3, tents=2, offset=(0.28, 0.38), tent_h=(0.13, 0.2), lean=(0, 4)),
                  variant('_b', 'b', 0.88, 1.0, 13, core=2, tents=2, offset=(0.28, 0.38), tent_h=(0.13, 0.2), lean=(2, 10),
                          ring_roles=(('mid', 'low'), ('low', 'mid'), ('low',))),
                  variant('_c', 'c', 0.98, 0.92, 11, core=3, tents=2, offset=(0.26, 0.36), tent_h=(0.13, 0.2), lean=(0, 4),
                          ring_roles=(('tall',), ('tall', 'mid'), ('mid', 'low')))],
        wind=0.4,
        material_props=dict(alphaCutoff=0.33, stiffness=1.0, drag=0.3, shiverDrag=0.03, shiverDirectionality=0.5,
                            aoMin=0.5, smoothnessMax=0.15, specular=0.12, normalScale=0.5, dryColor=[0.92, 0.92, 0.91],
                            colorNoiseSpread=0.3, puddleSink=0.15, sizeVariation=0.3, scaleFade=[100000, 40],
                            seasonKeep=2.0),
        nr=dict(renderDistance=110.0, shadows=False, densityInDistance=0.6), cast_shadows=False,
        hint=dict(density=(0.2, 0.5), clump=(8.0, 0.4), land={}, seasons=(1.0, 0.3, 1.0, 1.5), aboveSnow=True,
                  note='AboveSnow = true (set it by hand: the importer ignores this hint): last year\'s stalks stand '
                       'above the snow (winter x1.5 for the bristle of a dead fallow), base layer under living weeds')),
    'ruderal_mixed_low': dict(
        material='ruderal_mixed_low', size=(2048, 1024),
        regions=[strip('lo_a', 0, 0.0026, (0.45, 0.9), 0.3, 80.0, LOW_MIX, rows=256, pad=16),
                 strip('lo_b', 256, 0.0026, (0.45, 0.9), 0.3, 90.0, LOW_MIX, rows=256, pad=16),
                 *tiles('lo', 512, [(60, 0.0, LOW_MIX)] * 4, cover=0.72)],
        target=(0.088, 0.1, 0.06), saturation=0.8, shade=dict(base=0.6, height=0.45, ao=0.72),
        cards=dict(length=(0.45, 0.9), min_gap=0.15, kink=(6, 18), height_scale=(0.9, 1.1), skirt=0.05, seg_split=0.7,
                   taper=0.8, lean=(2, 10)),
        # два купола с рваным краем из разных плиток, свой поворот и относ — соседние пятна не складываются в ковёр
        variants=[variant('', 'a', 1.0, 1.0, 12, core=2, tents=2, offset=(0.26, 0.36), tent_h=(0.26, 0.4),
                          rim=(0.02, 0.04), tent_profile='dome'),
                  variant('_b', 'b', 0.9, 0.95, 11, core=2, tents=2, offset=(0.26, 0.36), tent_h=(0.24, 0.36),
                          rim=(0.02, 0.04), tent_profile='dome')],
        # плитка реже и рваней (край 0.86 ± 0.14 радиуса): с закрытой плиткой и краем у 0.97 купола подседа сверху
        # читались одинаковыми круглыми подушками
        tile_rag=2.2, tile_edge=0.86, tile_grade=0.85,
        wind=0.6,
        material_props=dict(alphaCutoff=0.35, stiffness=0.9, drag=0.6, shiverDrag=0.05, shiverDirectionality=0.6,
                            aoMin=0.5, smoothnessMax=0.22, specular=0.15, normalScale=0.6, dryColor=[0.94, 0.92, 0.84],
                            colorNoiseSpread=0.35, puddleSink=0.12, sizeVariation=0.2, scaleFade=[100000, 30],
                            seasonKeep=0.0),
        nr=dict(renderDistance=70.0, shadows=False, densityInDistance=0.3), cast_shadows=False,
        hint=dict(density=(0.25, 0.7), clump=(6.0, 0.3), land=dict(Track=0.3), seasons=(1.0, 1.0, 1.0, 0.0),
                  aboveSnow=False, note='filler between stands, on tracks, trampled patches and crater rims')),
}


def region_list(spec):
    out = []
    for r in spec['regions']:
        r = dict(r)
        r['tex_w'], r['tex_h'] = spec['size']
        out.append(r)
    return out


# ============================================================================================== заполнение областей
def sample_params(rng, params):
    out = {}
    for k, v in params.items():
        if isinstance(v, tuple) and len(v) == 2 and all(isinstance(x, (int, float)) for x in v):
            out[k] = rng.randint(*v) if all(isinstance(x, int) for x in v) and k == 'dead' else rng.uniform(*v)
        else:
            out[k] = v
    return out


def plant_label(kind, params):
    """Вид растения для раскладки (seg_kinds): полынь обыкновенная — mugwort, будылья — dead_<вид>."""
    if kind == 'wormwood' and params.get('vulgaris'):
        return 'mugwort'
    if kind == 'dead':
        return 'dead_' + params.get('kind', 'ragweed')
    return kind


def make_plant(rng, plants):
    w = [p[0] for p in plants]
    _, kind, params = rng.choices(plants, weights=w)[0]
    seed = rng.getrandbits(31)
    prng = random.Random(seed)
    kw = sample_params(prng, params)
    return PLANTS[kind](prng, **kw), plant_label(kind, params)


def segment_edges(rng, width, seg):
    edges = [0.0]
    while width - edges[-1] > seg[1]:
        edges.append(edges[-1] + rng.uniform(*seg))
    if width - edges[-1] < seg[0] * 0.7 and len(edges) > 1:
        edges.pop()
    edges.append(width)
    return edges


def fit_plant(rng, V, x0, x1, top):
    """Растение (вершины V) целиком в участок [x0, x1] по X и ниже top по Z: случайный поворот и масштаб 0.88–1.08
    (до 6 попыток), при нужде — сжатие по горизонтали до 20 %. Возвращает (матрица 3×3, сдвиг X) или None."""
    zmax = V[:, 2].max()
    s = min(rng.uniform(0.88, 1.08), top / max(zmax, 1e-3))
    for attempt in range(6):
        M = rot(UP, rng.uniform(0, 2 * math.pi)) * s
        X = V[:, :2] @ M[:2, :2].T
        lo, hi = X[:, 0].min(), X[:, 0].max()
        if (x1 - x0) - (hi - lo) >= 0:
            return M, rng.uniform(x0 - lo, x1 - hi)
        if attempt == 5 and (hi - lo) * 0.8 <= (x1 - x0):
            k = (x1 - x0) / (hi - lo)
            M2 = M @ np.diag([k, k, 1.0])
            X = V[:, :2] @ M2[:2, :2].T
            return M2, (x0 - X[:, 0].min())
    return None


class Pool:
    """Набор неповторимых растений области (строится один раз): в сцене они стоят экземплярами — каждое повёрнуто и
    отмасштабировано по-своему, в текстуре повтор не виден, а геометрия строится в 4–5 раз быстрее. Растение берётся
    по весам смеси (своя доля пула у каждой её строки): из пула наугад и с повтором после неудачной примерки участка
    смесь уезжала к узким растениям — полыни обыкновенной в средней полосе было 44 % вместо 15."""

    def __init__(self, rng, plants, size):
        self.rng = rng
        self.plants = plants
        self.size = size
        self.items = []   # (Geo, вид, вершины, строка смеси)
        total = sum(p[0] for p in plants)
        self.quota = [max(1, int(round(size * p[0] / total))) for p in plants]

    def pick_entry(self):
        return self.rng.choices(range(len(self.plants)), weights=[p[0] for p in self.plants])[0]

    def get(self, entry=None):
        e = self.pick_entry() if entry is None else entry
        mine = [i for i, it in enumerate(self.items) if it[3] == e]
        if len(mine) < self.quota[e]:
            g, kind = make_plant(self.rng, [self.plants[e]])
            self.items.append((g, kind, g.arrays()[0], e))
            return len(self.items) - 1
        return self.rng.choice(mine)


def populate_strip(rng, reg, pool_size=16):
    texel = reg['texel']
    W = reg['w'] * texel
    top = (reg['h'] - reg['pad']) * texel - 0.012
    edges = segment_edges(rng, W, reg['seg'])
    pool = Pool(rng, reg['mix'], pool_size)
    placements = []
    margin = 0.012
    skipped = 0
    seg_kinds = []
    for i in range(len(edges) - 1):
        x0, x1 = edges[i] + margin, edges[i + 1] - margin
        n = max(1, int(round(reg['density'] * (x1 - x0) * reg['depth'] * rng.uniform(0.85, 1.15))))
        # профиль участка: у трёх из четырёх — бугор (к концам растения ниже, до 0.45–0.65 роста): карточка с ровным
        # верхом до самого края делала из пятна цилиндр-«живую изгородь», бугры соседних пятен сливаются
        edge_min = rng.uniform(0.45, 0.65) if rng.random() < 0.75 else rng.uniform(0.8, 0.95)
        kinds = {}
        for _ in range(n):
            entry = pool.pick_entry()   # примерка — растениями той же строки смеси (иначе в узкий участок шли узкие)
            for _try in range(4):
                idx = pool.get(entry)
                fit = fit_plant(rng, pool.items[idx][2], x0, x1, top)
                if fit is not None:
                    M, x = fit
                    u = min(1.0, max(0.0, (x - x0) / max(x1 - x0, 1e-6)))
                    M = M * lerp(edge_min, 1.0, (1.0 - abs(2.0 * u - 1.0)) ** 0.7)
                    placements.append((idx, M, np.array([x, rng.uniform(0.0, reg['depth']), 0.0])))
                    label = pool.items[idx][1]
                    kinds[label] = kinds.get(label, 0) + 1
                    break
            else:
                skipped += 1
        seg_kinds.append(kinds)
    return pool, placements, dict(edges=edges, skipped=skipped, seg_kinds=seg_kinds)


def tile_more(rng, reg, pool, n, tries=5):
    """n растений верха заросли для шатра: в круге 0.85 spread, видимое (выше z_cut) — не дальше 1.2 spread (край
    шатра гасит мягкая рваная маска, compose_atlas)."""
    R = reg['spread']
    zc = reg['z_cut']
    out = []
    for _ in range(n * tries):
        if len(out) >= n:
            break
        idx = pool.get()
        V = pool.items[idx][2]
        M = rot(UP, rng.uniform(0, 2 * math.pi)) * rng.uniform(0.9, 1.08)
        Vr = V @ M.T
        above = Vr[:, 2] > zc * Vr[:, 2].max() if zc > 0 else np.ones(len(Vr), bool)
        if not above.any():
            continue
        for _ in range(30):
            a, r = rng.uniform(0, 2 * math.pi), R * 0.85 * math.sqrt(rng.random())
            off = np.array([r * math.cos(a), r * math.sin(a), 0.0])
            if np.hypot(Vr[above, 0] + off[0], Vr[above, 1] + off[1]).max() <= R * 1.2:
                out.append((idx, M, off))
                break
    return out


def populate_tile(rng, reg, pool_size=10):
    """Верх заросли для шатра: count растений для начала (досыпает bake_region до доли cover)."""
    pool = Pool(rng, reg['mix'], pool_size)
    return pool, tile_more(rng, reg, pool, reg['count']), {}


def grid_noise(h, w, cell_px, seed):
    """Гладкий шум 0..1 на сетке h × w с ячейкой cell_px (значения в узлах, smoothstep между ними)."""
    g = np.random.default_rng(seed)
    c = g.random((h // cell_px + 2, w // cell_px + 2))
    y = np.arange(h) / cell_px
    x = np.arange(w) / cell_px
    y0, x0 = np.floor(y).astype(int), np.floor(x).astype(int)
    fy, fx = y - y0, x - x0
    fy, fx = fy * fy * (3 - 2 * fy), fx * fx * (3 - 2 * fx)
    a, b = c[y0][:, x0], c[y0][:, x0 + 1]
    cc, d = c[y0 + 1][:, x0], c[y0 + 1][:, x0 + 1]
    top = a + (b - a) * fx[None]
    bottom = cc + (d - cc) * fx[None]
    return top + (bottom - top) * fy[:, None]


def tile_fade(reg, seed, rag=1.0, edge0=0.97):
    """Мягкий рваный край плитки шатра: 1 внутри, 0 у радиуса ~spread; край — гармоники по углу и шум 2–7 см (× rag),
    чтобы сверху не читался круг-«кувшинка» (у подседа rag 1.7: его шатры — главное, что видно с высоты). Внутри 0.8
    spread почти везде 1: плитка закрыта на cover, и шатёр сверху закрывает землю (с прежним краем от 0.62 радиуса
    половина круга была полупрозрачной)."""
    h, w = reg['h'], reg['w']
    yy, xx = np.mgrid[0:h, 0:w]
    dx = (xx - w / 2 + 0.5) * reg['texel']
    dy = (yy - h / 2 + 0.5) * reg['texel']
    r = np.hypot(dx, dy)
    th = np.arctan2(dy, dx)
    rr = random.Random(seed)
    k = sum(amp * np.sin(n * th + rr.uniform(0, 6.3)) for n, amp in ((2, 0.04), (3, 0.04), (5, 0.035), (7, 0.03),
                                                                     (11, 0.025), (17, 0.02), (23, 0.015)))
    coarse = grid_noise(h, w, max(4, int(0.07 / reg['texel'])), seed + 1) - 0.5
    fine = grid_noise(h, w, max(2, int(0.025 / reg['texel'])), seed + 2) - 0.5
    edge = reg['spread'] * (edge0 + rag * (k + 0.12 * coarse))
    # не дальше апофемы восьмиугольника шатра (прямые края шатра срезали бы рваный край): мягкий минимум, мелкий шум
    # (±1.4 см) — после него
    cap = reg['dome_r'] * math.cos(math.pi / 8) - 0.016
    edge = -0.025 * np.logaddexp(-edge / 0.025, -cap / 0.025) + 0.04 * reg['spread'] * fine
    return 1.0 - smoothstep(edge * 0.9, edge, r)


def place_pool(pool, placements, name, meshes=None):
    """Растения пула — меши (один раз, кэш meshes), расстановка — объекты-экземпляры (Cycles хранит меш один раз)."""
    meshes = {} if meshes is None else meshes
    dummy = bpy.data.materials.get('dummy') or bpy.data.materials.new('dummy')
    objs = []
    kinds = {}
    faces = 0
    zmax = 0.0
    for j, (idx, M, off) in enumerate(placements):
        if idx not in meshes:
            me = pool.items[idx][0].to_mesh(f'{name}_p{idx}')
            me.materials.append(dummy)
            meshes[idx] = me
        ob = bpy.data.objects.new(f'{name}_{j}', meshes[idx])
        bpy.context.scene.collection.objects.link(ob)
        ob.matrix_world = Matrix(((M[0, 0], M[0, 1], M[0, 2], off[0]), (M[1, 0], M[1, 1], M[1, 2], off[1]),
                                  (M[2, 0], M[2, 1], M[2, 2], off[2]), (0.0, 0.0, 0.0, 1.0)))
        objs.append(ob)
        g, kind, V = pool.items[idx][:3]
        kinds[kind] = kinds.get(kind, 0) + 1
        faces += g.faces
        zmax = max(zmax, float((V @ M.T)[:, 2].max()))
    return objs, dict(plants=len(placements), unique=len(pool.items), kinds=kinds, faces=faces, zmax=round(zmax, 3))


# ============================================================================================== запекание
def use_device(scene):
    scene.cycles.device = 'CPU'
    if DEVICE == 'CPU':
        return
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        prefs.compute_device_type = DEVICE
        prefs.get_devices()
        found = False
        for d in prefs.devices:
            d.use = d.type == DEVICE
            found |= d.use
        if found:
            scene.cycles.device = 'GPU'
    except Exception as e:  # noqa: BLE001
        print('DEVICE_FALLBACK', e)


def pass_material(kind, ao_distance=0.06, height_ref=1.0):
    if kind == 'normal':
        return eg.view_normal_material('pass_normal')
    m = bpy.data.materials.new('pass_' + kind)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    em = nt.nodes.new('ShaderNodeEmission')
    nt.links.new(em.outputs['Emission'], out.inputs['Surface'])
    if kind in ('albedo', 'smooth'):
        col = nt.nodes.new('ShaderNodeVertexColor')
        col.layer_name = 'Col'
        nt.links.new(col.outputs['Color' if kind == 'albedo' else 'Alpha'], em.inputs['Color'])
    elif kind == 'height':
        geo = nt.nodes.new('ShaderNodeNewGeometry')
        sep = nt.nodes.new('ShaderNodeSeparateXYZ')
        nt.links.new(geo.outputs['Position'], sep.inputs[0])
        nt.links.new(sep.outputs['Z'], em.inputs['Color'])
    else:
        ao = nt.nodes.new('ShaderNodeAmbientOcclusion')
        ao.samples = 16
        ao.inputs['Distance'].default_value = ao_distance
        nt.links.new(ao.outputs['AO'], em.inputs['Color'])
    return m


def read_exr(path):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)[::-1].copy()
    bpy.data.images.remove(img)
    return px


def unpremultiply(px):
    a = px[..., 3:4]
    rgb = np.where(a > 1e-4, px[..., :3] / np.maximum(a, 1e-4), 0.0)
    return np.concatenate([rgb, a], -1)


def push_pull(values, weight):
    """Заполнение прозрачного: значения из соседей по пирамиде; где вес > 0 — остаются свои. Нечётный край уровня
    дополняется нулевым весом (раньше он отрезался: низ полосы — где растения — выпадал из грубых уровней, и пустые
    места атласа оставались чёрными, до половины области), самый грубый уровень — среднее всего."""
    c = values * weight[..., None]
    w = weight.astype(np.float64).copy()
    levels = []
    while max(c.shape[:2]) > 1:
        levels.append((c, w))
        if c.shape[0] % 2:
            c = np.concatenate([c, np.zeros_like(c[:1])], 0)
            w = np.concatenate([w, np.zeros_like(w[:1])], 0)
        if c.shape[1] % 2:
            c = np.concatenate([c, np.zeros_like(c[:, :1])], 1)
            w = np.concatenate([w, np.zeros_like(w[:, :1])], 1)
        h2, w2 = c.shape[0] // 2, c.shape[1] // 2
        c = c.reshape(h2, 2, w2, 2, -1).sum((1, 3))
        w = w.reshape(h2, 2, w2, 2).sum((1, 3))
    total = float(w.sum())
    mean = c.reshape(-1, c.shape[-1]).sum(0) / max(total, 1e-8)
    col = np.where(w[..., None] > 1e-8, c / np.maximum(w, 1e-8)[..., None], mean)
    for c_l, w_l in reversed(levels):
        up = np.repeat(np.repeat(col, 2, 0), 2, 1)[:c_l.shape[0], :c_l.shape[1]]
        own = c_l / np.maximum(w_l, 1e-8)[..., None]
        t = np.clip(w_l, 0, 1)[..., None]
        col = own * t + up * (1 - t)
    return np.where(weight[..., None] > 0, values, col)


def tile_cover(scene, objs, reg, tmp, res=256):
    """Доля закрытого в круге 0.8 spread плитки — по альфе быстрого рендера (res², 1 сэмпл, эмиссия)."""
    mat = bpy.data.materials.get('pass_cover')
    if mat is None:
        mat = pass_material('smooth')
        mat.name = 'pass_cover'
    r = scene.render
    keep = (r.resolution_x, r.resolution_y, scene.cycles.samples, r.image_settings.file_format,
            r.image_settings.color_depth)
    r.resolution_x = r.resolution_y = res
    scene.cycles.samples = 1
    r.image_settings.file_format = 'PNG'
    r.image_settings.color_mode = 'RGBA'
    r.image_settings.color_depth = '8'
    path = os.path.join(tmp, 'cover.png')
    try:
        eg.render_with_override(scene, objs, mat, path)
    except RuntimeError:
        if scene.cycles.device != 'GPU':
            raise
        scene.cycles.device = 'CPU'
        eg.render_with_override(scene, objs, mat, path)
    r.resolution_x, r.resolution_y, scene.cycles.samples, r.image_settings.file_format, r.image_settings.color_depth = keep
    img = bpy.data.images.load(path, check_existing=False)
    a = np.array(img.pixels[:], np.float32).reshape(res, res, 4)[..., 3].copy()
    bpy.data.images.remove(img)
    yy, xx = np.mgrid[0:res, 0:res]
    rr = np.hypot(xx - res / 2 + 0.5, yy - res / 2 + 0.5) * (reg['w'] * reg['texel'] / res)
    return float((a[rr < 0.8 * reg['spread']] > 0.5).mean())


def bake_region(reg, tmp, seed):
    """Сцена области, рендер проходов в EXR; возвращает словарь массивов (строки сверху вниз) и статистику."""
    rng = random.Random(seed)
    eg.reset_scene()
    t0 = time.time()
    if reg['kind'] == 'strip':
        pool, placements, stats = populate_strip(rng, reg, reg.get('pool', 16))
    else:
        pool, placements, stats = populate_tile(rng, reg, reg.get('pool', 10))
    meshes = {}
    objs, st2 = place_pool(pool, placements, reg['name'], meshes)
    scene = bpy.context.scene
    world = bpy.data.worlds.new('w')
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Strength'].default_value = 0.0
    scene.world = world
    bpy.ops.mesh.primitive_plane_add(size=12, location=(reg['w'] * reg['texel'] * 0.5 if reg['kind'] == 'strip' else 0, 0, 0))
    bpy.context.active_object.visible_camera = False
    w = reg['w']
    h = reg['h'] - reg['pad']
    texel = reg['texel']
    eg.setup_card_render(scene, w, h, samples=16)
    use_device(scene)
    scene.render.filter_size = 1.0
    scene.cycles.max_bounces = 0
    scene.cycles.transparent_max_bounces = 0
    if reg['kind'] == 'strip':
        eg.ortho_camera(scene, (w * texel * 0.5, -2.0, h * texel * 0.5), (math.radians(90), 0, 0), w * texel)
    else:
        top = 4.0
        cam = eg.ortho_camera(scene, (0, 0, top), (0, 0, 0), w * texel)
        # шатёр закрыт, только если плитка закрыта: досыпать растения, пока круг 0.8 spread не закрыт на cover
        steps = []
        for it in range(4):
            cam.data.clip_end = top - reg['z_cut'] * st2['zmax'] if reg['z_cut'] > 0 else 100.0
            cov = tile_cover(scene, objs, reg, tmp)
            steps.append(round(cov, 3))
            if cov >= reg['cover'] or it == 3:
                break
            n = len(placements)
            per = 1.0 - (1.0 - min(cov, 0.97)) ** (1.0 / max(n, 1))
            need = math.log(1.0 - reg['cover']) / math.log(max(1.0 - per, 1e-6)) - n
            add = int(min(max(math.ceil(need * 1.1), 3), n * 1.5 + 4))
            placements = placements + tile_more(rng, reg, pool, add)
            for ob in objs:
                bpy.data.objects.remove(ob)
            objs, st2 = place_pool(pool, placements, reg['name'], meshes)
        stats['cover_steps'] = steps
        cam.data.clip_end = top - reg['z_cut'] * st2['zmax'] if reg['z_cut'] > 0 else 100.0
    stats.update(st2)
    stats['build_s'] = round(time.time() - t0, 1)
    out = {}
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.filter_size = 1.0
    scene.cycles.max_bounces = 0
    scene.cycles.transparent_max_bounces = 0
    kinds = ['albedo', 'smooth', 'normal', 'ao'] + (['height'] if reg['kind'] == 'tile' else [])
    t1 = time.time()
    for kind in kinds:
        mat = pass_material(kind, ao_distance=0.05 if reg['kind'] == 'tile' else 0.07)
        scene.cycles.samples = 24 if kind == 'ao' else 12
        path = os.path.join(tmp, f'{kind}.exr')
        try:
            eg.render_with_override(scene, objs, mat, path)
        except RuntimeError as e:
            # память видеокарты делят и другие программы: тяжёлую область — на процессоре
            if scene.cycles.device != 'GPU':
                raise
            print('GPU_FALLBACK', reg['name'], kind, e, flush=True)
            scene.cycles.device = 'CPU'
            eg.render_with_override(scene, objs, mat, path)
        out[kind] = read_exr(path)
    stats['render_s'] = round(time.time() - t1, 1)
    print('REGION', reg['name'], json.dumps({k: v for k, v in stats.items() if k != 'edges'}), flush=True)
    return out, stats


def compose_atlas(name, spec, out_dir, only=None, cache_dir=None):
    """Запекает области ассета (only — только эти, остальные из кэша), притемняет, приводит к палитре, пишет текстуры
    и раскладку (<имя материала>_layout.json) в папку ассета. Кэш проходов — во временной папке (сотни МБ)."""
    tmp = os.path.join(cache_dir or CACHE_DIR, name)
    os.makedirs(tmp, exist_ok=True)
    folder = os.path.join(out_dir, name)
    os.makedirs(folder, exist_ok=True)
    cache_path = os.path.join(tmp, 'regions.npz')
    stats_path = os.path.join(tmp, 'stats.json')
    cache = dict(np.load(cache_path)) if os.path.exists(cache_path) else {}
    all_stats = json.load(open(stats_path, encoding='utf-8')) if os.path.exists(stats_path) else {}
    regs = region_list(spec)
    raw = {}
    for reg in regs:
        key = reg['name']
        if (only is None or key in only) or key + '_alb' not in cache:
            passes, stats = bake_region(reg, tmp, zlib.crc32((name + key).encode()))
            all_stats[key] = stats
            alb = unpremultiply(passes['albedo'])
            a = np.clip(alb[..., 3], 0, 1)
            if reg['kind'] == 'tile':
                a = a * tile_fade(reg, zlib.crc32((name + key + 'fade').encode()), spec.get('tile_rag', 1.0),
                                  spec.get('tile_edge', 0.97))
            n = unpremultiply(passes['normal'])[..., :3] * 2 - 1
            ao = unpremultiply(passes['ao'])[..., 0]
            sm = unpremultiply(passes['smooth'])[..., 0]
            if reg['kind'] == 'tile':
                z = unpremultiply(passes['height'])[..., 0]
            else:
                h = reg['h'] - reg['pad']
                z = np.repeat(((h - np.arange(h) - 0.5) * reg['texel'])[:, None], reg['w'], 1)
            cache[key + '_alb'] = np.concatenate([alb[..., :3], a[..., None]], -1).astype(np.float32)
            cache[key + '_nrm'] = n.astype(np.float32)
            cache[key + '_aoz'] = np.stack([ao, sm, z], -1).astype(np.float32)
        raw[key] = (cache[key + '_alb'], cache[key + '_nrm'], cache[key + '_aoz'])
    np.savez(cache_path, **cache)
    with open(stats_path, 'w', encoding='utf-8') as f:
        json.dump(all_stats, f, indent=1)
    cutoff = spec['material_props']['alphaCutoff']
    sh = spec['shade']
    shaded, visible = {}, {}
    for reg in regs:
        alb, nrm, aoz = raw[reg['name']]
        a = alb[..., 3]
        ao, z = aoz[..., 0], aoz[..., 2]
        visible_top = float(np.percentile(z[a > 0.5], 97)) if (a > 0.5).any() else 1.0
        f = lerp(sh['base'], 1.0, smoothstep(0.0, sh['height'] * visible_top, z)) * lerp(sh['ao'], 1.0, np.clip(ao, 0, 1))
        shaded[reg['name']] = alb[..., :3] * f[..., None]
        visible[reg['name']] = a > cutoff
    # полосы — к палитре (target — среднее их видимых текселей); плитки — к 0.86 верхней четверти полос: сверху видны
    # освещённые верхушки (плитки, притемнённые как нижний ярус, были вдвое темнее, и заросль над головой чернела)
    strips = [r for r in regs if r['kind'] == 'strip']
    tiles_ = [r for r in regs if r['kind'] == 'tile']
    graded = {}

    def grade(group, target):
        flat = np.concatenate([shaded[r['name']].reshape(-1, 3) for r in group])
        wts = np.concatenate([visible[r['name']].ravel() for r in group]).astype(np.float32)
        out = eg.grade_albedo(flat, target, spec['saturation'], wts)
        pos = 0
        for r in group:
            h, w = shaded[r['name']].shape[:2]
            graded[r['name']] = out[pos:pos + h * w].reshape(h, w, 3)
            pos += h * w

    grade(strips, spec['target'])
    tops = []
    for reg in strips:
        z = raw[reg['name']][2][..., 2]
        vis = visible[reg['name']]
        if vis.any():
            sel = vis & (z >= 0.75 * float(np.percentile(z[vis], 97)))
            tops.append(graded[reg['name']][sel])
    top_mean = np.concatenate(tops).mean(0) if tops else np.asarray(spec['target'], np.float32)
    top_lum = float(top_mean @ np.array([0.2126, 0.7152, 0.0722]))
    for t in tiles_:   # каждую плитку отдельно: плитка с тёмной полынью обыкновенной иначе выходила на 0.78
        grade([t], top_mean * spec.get('tile_grade', 0.86))
    W, H = spec['size']
    albedo = np.zeros((H, W, 4), np.float32)
    normal = np.zeros((H, W, 3), np.float32) + np.array([0.5, 0.5, 1.0], np.float32)
    mask = np.zeros((H, W, 4), np.float32)
    layout = {}
    for reg in regs:
        alb, nrm, aoz = raw[reg['name']]
        h, w = alb.shape[:2]
        rgb = graded[reg['name']]
        a = alb[..., 3]
        rgb = push_pull(rgb, (a > 0.02).astype(np.float32) * a)
        n = nrm / np.maximum(np.linalg.norm(nrm, axis=-1, keepdims=True), 1e-6)
        n = push_pull(n, (a > 0.3).astype(np.float32))
        n = n / np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6) * 0.5 + 0.5
        filled = push_pull(aoz[..., :2], (a > 0.3).astype(np.float32))
        msk = np.zeros((h, w, 4), np.float32)
        msk[..., 1] = np.clip(filled[..., 0], 0, 1)
        msk[..., 3] = np.clip(filled[..., 1], 0, 1)
        al = np.concatenate([rgb, a[..., None]], -1)
        if reg['pad']:
            def rep(arr):
                return np.concatenate([arr, np.repeat(arr[-1:], reg['pad'], 0)], 0)
            al, n, msk = rep(al), rep(n), rep(msk)
        x, y = reg['x'], reg['y']
        albedo[y:y + reg['h'], x:x + reg['w']] = al
        normal[y:y + reg['h'], x:x + reg['w']] = n
        mask[y:y + reg['h'], x:x + reg['w']] = msk
        info = {k: v for k, v in reg.items() if k not in ('mix',)}
        st = all_stats.get(reg['name'], {})
        av = al[..., 3]
        vis = av > cutoff
        if reg['kind'] == 'strip':
            base_px = reg['h'] - reg['pad']
            rows = np.where((av[:base_px] > 0.35).mean(1) > 0.002)[0]
            top_row = int(rows.min()) if len(rows) else 0
            info['base_px'] = base_px
            info['content_h'] = round((base_px - top_row) * reg['texel'] + 0.004, 4)
            info['edges_px'] = [int(round(e / reg['texel'])) for e in st.get('edges', [0.0, reg['w'] * reg['texel']])]
            info['edges_px'][-1] = reg['w']
            info['coverage_by_height'] = [round(float((av[base_px - 1 - int(zz / reg['texel'])] > cutoff).mean()), 3)
                                          for zz in (0.05, 0.3, 0.6, 0.9, 1.2, 1.5, 1.8, 2.1)
                                          if int(zz / reg['texel']) < base_px]
            # непрозрачность среза до верха растений (без пустых строк над ними)
            info['coverage_content'] = round(float(vis[top_row:base_px].mean()), 3)
            info['seg_kinds'] = st.get('seg_kinds', [])
        else:
            yy, xx = np.mgrid[0:reg['h'], 0:reg['w']]
            rr = np.hypot(xx - reg['w'] / 2 + 0.5, yy - reg['h'] / 2 + 0.5) * reg['texel']
            info['coverage_disc'] = round(float(vis[rr < reg['spread'] * 0.8].mean()), 3)
        lum = (al[..., :3] @ np.array([0.2126, 0.7152, 0.0722]))[vis]
        info['mean_lum'] = round(float(lum.mean()), 4) if vis.any() else None
        info['mean_linear'] = [round(float(v), 4) for v in al[..., :3][vis].mean(0)] if vis.any() else None
        if reg['kind'] == 'tile' and vis.any():
            info['lum_vs_strip_top'] = round(float(lum.mean()) / max(top_lum, 1e-6), 3)
        info['coverage'] = round(float(vis.mean()), 3)
        info['stats'] = {k: v for k, v in st.items() if k not in ('edges', 'seg_kinds')}
        layout[reg['name']] = info
    prefix = spec['material']
    eg.save_png(albedo, os.path.join(folder, prefix + '_albedo.png'))
    eg.save_png(normal, os.path.join(folder, prefix + '_normal.png'), linear=True)
    eg.save_png(mask, os.path.join(folder, prefix + '_mask.png'), linear=True)
    with open(os.path.join(folder, prefix + '_layout.json'), 'w', encoding='utf-8') as f:
        json.dump(dict(size=spec['size'], regions=layout, strip_top_lum=round(top_lum, 4)), f, indent=1)
    vis_all = albedo[..., 3] > cutoff
    lum_all = albedo[..., :3] @ np.array([0.2126, 0.7152, 0.0722])
    print('ATLAS_OK', name, json.dumps(dict(
        mean_lum=round(float(lum_all[vis_all].mean()), 4), strip_top_lum=round(top_lum, 4),
        coverage={k: v.get('coverage_content', v.get('coverage_disc')) for k, v in layout.items()},
        tile_vs_top={k: v.get('lum_vs_strip_top') for k, v in layout.items() if v['kind'] == 'tile'},
        black_holes=round(float((albedo[..., :3].max(-1) < 1e-4).mean()), 4))), flush=True)
    return layout


# ============================================================================================== карточки
def region_uv(reg, px, py):
    return (px / reg['tex_w'], 1.0 - py / reg['tex_h'])


def wind_alpha(z, H, amax, power=1.3):
    if z <= 0:
        return WIND_MIN
    return WIND_MIN + (amax - WIND_MIN) * min(1.0, z / max(H, 1e-3)) ** power


ROLE_BY_RING = (('tall', 'mid'), ('mid', 'tall', 'low'), ('low', 'mid'))


def pick_segment(rng, strips, target_m, used, roles, prefer=None):
    """Участок полосы с длиной ближе всего к нужной (с небольшим разбросом) из полос с ролью из roles (место карточки в
    пятне: середина, кольцо, край); prefer — вид растений, которому участок отдаётся охотнее (канделябр татарника,
    метёлки мелколепестника); один участок — одна карточка в пятне, пока свободные есть. Возвращает (полоса, № участка)."""
    pool = [r for r in strips if r.get('role', 'mid') in roles] or strips
    cands = []
    for reg in pool:
        e = reg['edges_px']
        cover = reg.get('seg_cover') or [1.0] * (len(e) - 1)
        kinds = reg.get('seg_kinds') or [{}] * (len(e) - 1)
        for i in range(len(e) - 1):
            if cover[i] < 0.03:
                continue   # участок без растений (широкое растение не влезло) — карточка была бы пустой
            L = (e[i + 1] - e[i]) * reg['texel']
            key = (reg['name'], i)
            score = abs(L - target_m) / target_m + rng.uniform(0, 0.15) + (5.0 if key in used else 0.0)
            if prefer and prefer in kinds[i]:
                score -= 0.8
            cands.append((score, reg, i, key))
    cands.sort(key=lambda c: c[0])
    _, reg, i, key = cands[0]
    used.add(key)
    return reg, i


def crown_normal(p, centre, up=0.5):
    """Нормаль «сферы кроны»: normalize(normalize(p − centre) + up·вверх), centre — середина пятна на 0.3 роста."""
    v = np.asarray(p, np.float64) - centre
    n = float(np.linalg.norm(v))
    return unit((v / n if n > 1e-6 else UP) + UP * up)


def vertical_card(mb, normals, rng, reg, seg, centre, yaw, cc, amax, H_wind, crown, max_segs, hs, lean, lean_az):
    """Вертикальная карточка: 2–3 звена с изломом в плане, один ряд по высоте и подземная юбка (берёт строки повтора
    низа полосы); высота — растений своего участка (seg_h: пустая альфа над ними только тратила заливку); верх наклонён
    на lean градусов к lean_az. U — участок полосы (иногда зеркально), V — от земли вверх 1:1 × hs (к краю пятна и у
    мелких вариантов карточки ниже и короче). Нормали — «сфера кроны» пятна."""
    e = reg['edges_px']
    u0, u1 = e[seg], e[seg + 1]
    L = (u1 - u0) * reg['texel'] * hs
    H = (reg.get('seg_h') or [reg['content_h']] * (len(e) - 1))[seg] * hs
    segs = min(max_segs, 2 if L < cc['seg_split'] else 3)
    d = np.array([math.cos(yaw), math.sin(yaw), 0.0])
    pts = [np.zeros(3)]
    for k in range(segs):
        if k:
            d = rot(UP, math.radians(rng.uniform(*cc['kink'])) * rng.choice([-1, 1])) @ d
        pts.append(pts[-1] + d * L / segs)
    pts = np.array(pts)
    pts -= (pts[0] + pts[-1]) * 0.5
    pts += np.array([centre[0], centre[1], 0.0])
    lean_v = np.array([math.cos(lean_az), math.sin(lean_az), 0.0]) * math.tan(math.radians(lean)) * H
    zs = [-cc['skirt'] * hs, 0.0, H]
    mirror = rng.random() < 0.5
    base_v = reg['y'] + reg['base_px']
    grid = []
    for z in zs:
        row = []
        for k in range(segs + 1):
            t = max(z, 0.0) / H
            p = pts[k] + lean_v * t ** 1.3 + UP * z
            frac = k / segs if not mirror else 1 - k / segs
            u_px = reg['x'] + u0 + (u1 - u0) * frac
            v_px = base_v + 0.7 * reg['pad'] if z < 0 else base_v - (z / hs) / reg['texel']
            n = crown_normal(p - UP * min(z, 0.0), crown)
            row.append((mb.vert(tuple(p)), region_uv(reg, u_px, v_px), (1.0, 1.0, 1.0, wind_alpha(z, H_wind, amax)), n))
        grid.append(row)
    for r in range(len(zs) - 1):
        for k in range(segs):
            a, b, c, f = grid[r][k], grid[r][k + 1], grid[r + 1][k + 1], grid[r + 1][k]
            mb.face([a[0], b[0], c[0], f[0]], 0, [a[1], b[1], c[1], f[1]], None, [a[2], b[2], c[2], f[2]])
            normals.extend([a[3], b[3], c[3], f[3]])
    return H, L


def tent(mb, normals, rng, reg, centre, h, rim, sides, amax, H_wind, crown, scale=1.0, profile='cone'):
    """«Шатёр» — верх заросли сверху: восьмиугольник веером (вершина на h, край почти у земли — rim); плитка
    спроецирована сверху и повёрнута, в мире — × scale. Сверху он закрывает землю между вертикальными карточками, сбоку
    спрятан в нижней части заросли (плоский купол на 0.6 роста читался сбоку парящей тарелкой). Профиль dome — с
    приподнятым кольцом на 0.55 радиуса (купол подседа, а не плоский «коврик»). Нормали — «сфера кроны», ближе к
    «вверх». Возвращает число треугольников."""
    R = reg['dome_r']
    yaw = rng.uniform(0, 2 * math.pi)
    cx_px, cy_px = reg['x'] + reg['w'] / 2, reg['y'] + reg['h'] / 2

    def vtx(lx, ly, z):
        wx = (lx * math.cos(yaw) - ly * math.sin(yaw)) * scale
        wy = (lx * math.sin(yaw) + ly * math.cos(yaw)) * scale
        p = np.array([centre[0] + wx, centre[1] + wy, z])
        return (mb.vert(tuple(p)), region_uv(reg, cx_px + lx / reg['texel'], cy_px - ly / reg['texel']),
                (1.0, 1.0, 1.0, wind_alpha(z, H_wind, amax)), unit(crown_normal(p, crown, 0.0) * 0.6 + UP))

    def face(vs):
        mb.face([v[0] for v in vs], 0, [v[1] for v in vs], None, [v[2] for v in vs])
        normals.extend([v[3] for v in vs])

    apex = vtx(0.0, 0.0, h)
    rings = [(0.55, True), (1.0, False)] if profile == 'dome' else [(1.0, False)]
    ring_v = []
    for frac, raised in rings:
        row = []
        for k in range(sides):
            ang = (k + 0.5) * 2 * math.pi / sides
            z = rim + (h - rim) * rng.uniform(0.55, 0.68) if raised else min(h * 0.8, rim * rng.uniform(0.7, 1.3))
            row.append(vtx(R * frac * math.cos(ang), R * frac * math.sin(ang), z))
        ring_v.append(row)
    for k in range(sides):
        face([apex, ring_v[0][k], ring_v[0][(k + 1) % sides]])
    for inner, outer in zip(ring_v[:-1], ring_v[1:]):
        for k in range(sides):
            k1 = (k + 1) % sides
            face([inner[k], outer[k], outer[k1], inner[k1]])
    return sides * (2 * len(rings) - 1)


def card_plan(rng, var, cc):
    """Карточки пятна: core крест-накрест у середины (повороты через π/core ± 18°), остальные — по кругу spread
    (0.32–0.79 радиуса) с промежутком min_gap, у каждой свой случайный поворот (у семейств параллельных карточек целое
    семейство разом вставало ребром)."""
    R = var['spread']
    core = min(var.get('core', 3), var['cards'])
    plan, centres = [], []
    th0 = rng.uniform(0, math.pi)
    for k in range(core):
        c = np.array([rng.uniform(-1, 1), rng.uniform(-1, 1)]) * 0.12 * R
        plan.append(dict(th=th0 + k * math.pi / core + math.radians(rng.uniform(-18, 18)), centre=c, core=True))
        centres.append(c)
    for k in range(var['cards'] - core):
        p = np.zeros(2)
        for _ in range(300):
            a, r = rng.uniform(0, 2 * math.pi), R * math.sqrt(rng.uniform(0.1, 0.62))
            p = np.array([r * math.cos(a), r * math.sin(a)])
            if all(np.linalg.norm(p - q) >= cc['min_gap'] for q in centres):
                break
        centres.append(p)
        plan.append(dict(th=rng.uniform(0, math.pi), centre=p, core=False))
    return plan


def build_patch(name, spec, layout, var):
    cc = spec['cards']
    rng = random.Random(zlib.crc32((name + '_' + var['seed'] + '_patch').encode()))
    regs = layout['regions']
    strips = [r for r in regs.values() if r['kind'] == 'strip']
    tiles_ = [r for r in regs.values() if r['kind'] == 'tile']
    for r in regs.values():
        r['tex_w'], r['tex_h'] = layout['size']
    s = var['scale']
    R = var['spread']
    H_ref = max(r['content_h'] for r in strips) * s
    crown = np.array([0.0, 0.0, 0.3 * H_ref])
    ring_roles = var.get('ring_roles') or ROLE_BY_RING
    lo, hi = var.get('lean', cc['lean'])
    profile = var.get('tent_profile', 'cone')
    tent_tris = 8 * (3 if profile == 'dome' else 1)
    mb = eg.MeshBuilder()
    normals = []
    plan = card_plan(rng, var, cc)
    used = set()
    heights, lengths, kinds = [], [], {}
    for i, card in enumerate(plan):
        # место в пятне: к краю карточки ниже и короче (из низкой полосы) — пятно бугром заходит в соседние
        r0 = 0.0 if card['core'] else float(np.linalg.norm(card['centre'])) / R
        ring = 0 if r0 < 0.35 else (1 if r0 < 0.72 else 2)
        hf = lerp(1.0, cc.get('taper', 0.6), float(smoothstep(0.25, 1.0, r0))) * rng.uniform(0.92, 1.06)
        hs = hf * s * rng.uniform(*cc['height_scale'])
        target = lerp(cc['length'][1], cc['length'][0], min(1.0, r0)) * rng.uniform(0.85, 1.1)
        prefer = var.get('prefer') if card['core'] and (i == 0 or var.get('prefer_core')) else None
        reg, seg = pick_segment(rng, strips, target / hs, used, ring_roles[ring], prefer)
        for k in (reg.get('seg_kinds') or [{}] * len(reg['edges_px']))[seg]:
            kinds[k] = kinds.get(k, 0) + 1
        L = (reg['edges_px'][seg + 1] - reg['edges_px'][seg]) * reg['texel'] * hs
        yaw = card['th'] + (math.pi if rng.random() < 0.5 else 0.0)
        d = np.array([math.cos(yaw), math.sin(yaw)])
        c = np.array(card['centre'], np.float64)
        for _ in range(40):   # концы карточки — в круге spread
            if max(np.linalg.norm(c + d * L / 2), np.linalg.norm(c - d * L / 2)) <= R or np.linalg.norm(c) < 0.02:
                break
            c *= 0.9
        if card['core'] or np.linalg.norm(c) < 0.05:
            lean, lean_az = rng.uniform(0.0, 1.5), rng.uniform(0, 2 * math.pi)
        else:
            # наружу — к свету, не больше lean[1] (полынь 6°: веер краевых карточек до 21° делал из куртины осколки)
            lean = rng.uniform(lo, hi) * (0.6 if ring == 1 else 1.0)
            lean_az = math.atan2(c[1], c[0]) + rng.uniform(-0.5, 0.5)
        # бюджет: 3 звена, только если на остальные карточки (по 2 звена) и шатры хватит до TRI_LIMIT
        rest = (len(plan) - i - 1) * 8 + var['tents'] * tent_tris
        max_segs = 3 if mb.triangle_count + 12 + rest <= TRI_LIMIT else 2
        H, L = vertical_card(mb, normals, rng, reg, seg, c, yaw, cc, spec['wind'], H_ref, crown, max_segs, hs, lean,
                             lean_az)
        heights.append(H)
        lengths.append(L)
    # шатры — в промежутки между карточками: от середины на offset, по кругу через 360°/n ± 25°, из разных плиток
    a0 = rng.uniform(0, 2 * math.pi)
    order = list(range(len(tiles_)))
    rng.shuffle(order)
    tents = []
    for k in range(var['tents']):
        a = a0 + k * 2 * math.pi / var['tents'] + rng.uniform(-0.45, 0.45)
        off = rng.uniform(*var['tent_offset'])
        reg = tiles_[order[k % len(order)]]
        h = H_ref * rng.uniform(*var['tent_height'])
        rim = rng.uniform(*var.get('rim', (0.01, 0.04))) * s
        tent(mb, normals, rng, reg, (off * math.cos(a), off * math.sin(a)), h, rim, 8, spec['wind'], H_ref, crown, s,
             profile)
        tents.append(dict(tile=reg['name'], offset=round(off, 2), r=round(reg['dome_r'] * s, 2), apex=round(h, 2)))
    mats = [bpy.data.materials.new(spec['material'])]
    obj = mb.to_object(name, mats)
    eg.set_custom_normals(obj, normals)
    return obj, mb.triangle_count, dict(cards=len(plan), tents=tents, card_lengths=[round(v, 2) for v in lengths],
                                        card_heights=[round(v, 2) for v in heights], kinds=kinds)


def segment_cover(albedo_path, layout, cutoff):
    """По альфе готового атласа — в layout: доля непрозрачного в каждом участке полос (seg_cover) и высота его растений
    (seg_h, м: карточка не выше них)."""
    img = bpy.data.images.load(albedo_path, check_existing=False)
    w, h = img.size
    a = np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1, :, 3].copy()
    bpy.data.images.remove(img)
    for reg in layout['regions'].values():
        if reg['kind'] != 'strip':
            continue
        block = a[reg['y']:reg['y'] + reg['base_px'], reg['x']:reg['x'] + reg['w']] > cutoff
        e = reg['edges_px']
        cover, tops = [], []
        for i in range(len(e) - 1):
            sub = block[:, e[i]:e[i + 1]]
            cover.append(round(float(sub.mean()), 4) if sub.size else 0.0)
            rows = np.where(sub.sum(1) >= max(2, int(0.004 * max(sub.shape[1], 1))))[0]
            top = int(rows.min()) if len(rows) else reg['base_px']
            tops.append(round(min((reg['base_px'] - top) * reg['texel'] + 0.012, reg['content_h']), 4))
        reg['seg_cover'] = cover
        reg['seg_h'] = tops


def material_entry(spec, layout):
    mid = spec['material']
    strips = [r for r in layout['regions'].values() if r['kind'] == 'strip']
    return dict(id=mid, role='grass', albedo=mid + '_albedo.png', normal=mid + '_normal.png', mask=mid + '_mask.png',
                alphaDrag=1.0, initialBend=1.0, healthyColor=[1.0, 1.0, 1.0], rotorWashClear=0.0,
                invertBackfaceNormal=0.0, aoMax=1.0, smoothnessMin=0.0, distanceControl=1.0, heightDrag=0.0,
                windColorMix=0.0, texel=round(float(np.mean([r['texel'] for r in strips])), 5),
                **spec['material_props'])


def profile_hint(name, spec):
    """Подсказка записи профиля (FlyPointDetailEntry; имена полей — как в инспекторе): плотность — на вид, по записи на
    каждый префаб-вариант поровну (DensityPerPrefab); бурьян — только на брошенной земле (LandScales: Fallow 1, остальные
    группы 0, кроме названных); стоит прямо (AlignToGround 0); размер — как построено (Nature Renderer берёт наибольшие
    WidthRange/HeightRange; разброс даёт sizeVariation материала)."""
    h = spec['hint']
    n = len(spec['variants'])
    lo, hi = h['density']
    land = dict(Meadow=0.0, Field=0.0, FieldEdge=0.0, Forest=0.0, Wetland=0.0, Settlement=0.0, Track=0.0, Barren=0.0,
                Fallow=1.0)
    land.update(h.get('land') or {})
    sp, su, au, wi = h['seasons']
    return dict(species=name, prefabs=['prefab_' + name + v['suffix'] for v in spec['variants']],
                DensityPerSquareMeter=[lo, hi], DensityPerPrefab=[round(lo / n, 3), round(hi / n, 3)],
                ClumpScale=h['clump'][0], ClumpStrength=h['clump'][1], PatchThreshold=0.0,
                LandScales=land, SatelliteScales=dict(Green=1.0, Yellow=1.0, Dark=1.0, Bare=0.6),
                SeasonScales=dict(Spring=sp, Summer=su, Autumn=au, Winter=wi),
                AboveSnow=bool(h['aboveSnow']), AlignToGround=0, KeepApart=False,
                WidthRange=[0.9, 1.0], HeightRange=[0.9, 1.0], note=h['note'])


def build_assets(out_dir, names):
    """Пятна всех вариантов ассета: FBX и manifest.json в папку варианта (<имя><suffix>), текстуры — копии атласа из
    папки ассета (импортёр берёт файлы из папки манифеста; в Unity у вариантов один атлас и один материал)."""
    report = {}
    for name in names:
        spec = ASSETS[name]
        mid = spec['material']
        folder = os.path.join(out_dir, name)
        with open(os.path.join(folder, mid + '_layout.json'), encoding='utf-8') as f:
            layout = json.load(f)
        segment_cover(os.path.join(folder, mid + '_albedo.png'), layout, spec['material_props']['alphaCutoff'])
        hint = profile_hint(name, spec)
        for var in spec['variants']:
            vname = name + var['suffix']
            vfolder = os.path.join(out_dir, vname)
            os.makedirs(vfolder, exist_ok=True)
            if os.path.normcase(os.path.abspath(vfolder)) != os.path.normcase(os.path.abspath(folder)):
                for suffix in ('albedo', 'normal', 'mask'):
                    shutil.copyfile(os.path.join(folder, f'{mid}_{suffix}.png'), os.path.join(vfolder, f'{mid}_{suffix}.png'))
            eg.reset_scene()
            obj, tris, stats = build_patch(vname, spec, layout, var)
            if tris > TRI_LIMIT:
                raise RuntimeError(f'{vname}: {tris} triangles > {TRI_LIMIT}')
            me = obj.data
            V = np.array([v.co[:] for v in me.vertices])
            alphas = [d.color[3] for d in me.color_attributes['Col'].data]
            card_area = float(sum(p.area for p in me.polygons))
            eg.export_fbx([obj], os.path.join(vfolder, vname + '.fbx'))
            size = [round(float(V[:, i].max() - V[:, i].min()), 3) for i in range(3)]
            height = round(float(V[:, 2].max()), 3)
            manifest = dict(name=vname, type='detail', group=GROUP, height=height, trunkRadius=0.0, colliderHeight=0.0,
                            evergreen=False,
                            lods=[dict(fbx=vname + '.fbx', screen=0.0, submeshes=[mid], castShadows=spec['cast_shadows'],
                                       triangles=tris)],
                            materials=[material_entry(spec, layout)], nr=dict(spec['nr']), size=size, triangles=tris,
                            aboveSnow=bool(spec["hint"]["aboveSnow"]), profileHint=hint)
            with open(os.path.join(vfolder, 'manifest.json'), 'w', encoding='utf-8') as f:
                json.dump(manifest, f, indent=1)
            report[vname] = dict(triangles=tris, size=size, height=height, min_alpha_byte=int(round(min(alphas) * 255)),
                                 max_alpha=round(max(alphas), 3), card_area=round(card_area, 2),
                                 card_area_per_m2=round(card_area * hint['DensityPerSquareMeter'][1], 1), **stats)
            print('ASSET_OK', vname, json.dumps(report[vname]), flush=True)
    rp = os.path.join(out_dir, 'ruderal_report.json')
    old = json.load(open(rp, encoding='utf-8')) if os.path.exists(rp) else {}
    old.update(report)
    with open(rp, 'w', encoding='utf-8') as f:
        json.dump(old, f, indent=1)
    return report



# ============================================================================================== предпросмотр
def unity_mips(rgba_px, cutoff=None, levels=7):
    """Мипы как у импортёра Unity: среднее 2×2 (линейно), альфа с сохранением покрытия при пороге cutoff по всей
    текстуре (mipMapsPreserveCoverage)."""
    out = [rgba_px]
    cur = rgba_px
    base_cov = float((rgba_px[..., 3] > cutoff).mean()) if cutoff is not None else None
    for _ in range(levels - 1):
        h, w = cur.shape[0] // 2, cur.shape[1] // 2
        cur = cur.reshape(h, 2, w, 2, -1).mean((1, 3))
        nxt = cur.copy()
        if cutoff is not None:
            a = cur[..., 3]
            lo, hi = 0.1, 16.0
            for _ in range(30):
                mid = (lo + hi) / 2
                if float((np.clip(a * mid, 0, 1) > cutoff).mean()) < base_cov:
                    lo = mid
                else:
                    hi = mid
            nxt[..., 3] = np.clip(a * (lo + hi) / 2, 0, 1)
        out.append(nxt)
    return out


def load_png(path, srgb):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4).copy()  # строки снизу вверх
    bpy.data.images.remove(img)
    if srgb:
        rgb = px[..., :3]
        px[..., :3] = np.where(rgb <= 0.04045, rgb / 12.92, ((rgb + 0.055) / 1.055) ** 2.4)
    return px


def make_image(name, px, srgb=False):
    """Картинка для Cycles (8 бит: память GPU): альбедо — в sRGB, данные — как есть."""
    h, w = px.shape[:2]
    data = np.ascontiguousarray(px, np.float32).copy()
    if srgb:
        rgb = np.clip(data[..., :3], 0, 1)
        data[..., :3] = np.where(rgb <= 0.0031308, rgb * 12.92, 1.055 * rgb ** (1 / 2.4) - 0.055)
    img = bpy.data.images.new(name, w, h, alpha=True, float_buffer=False)
    img.colorspace_settings.name = 'sRGB' if srgb else 'Non-Color'
    img.alpha_mode = 'CHANNEL_PACKED'
    img.pixels.foreach_set(np.clip(data, 0, 1).ravel())
    img.pack()
    return img


class Nodes:
    def __init__(self, mat):
        self.nt = mat.node_tree
        self.nt.nodes.clear()

    def new(self, kind, **props):
        n = self.nt.nodes.new(kind)
        for k, v in props.items():
            setattr(n, k, v)
        return n

    def link(self, a, b):
        self.nt.links.new(a, b)

    def put(self, socket, value):
        if isinstance(value, (int, float, tuple)):
            socket.default_value = value
        else:
            self.link(value, socket)

    def math(self, op, a, b=None, clamp=False):
        n = self.new('ShaderNodeMath', operation=op, use_clamp=clamp)
        self.put(n.inputs[0], a)
        if b is not None:
            self.put(n.inputs[1], b)
        return n.outputs[0]

    def vmath(self, op, a, b=None, scale=None, out=0):
        n = self.new('ShaderNodeVectorMath', operation=op)
        self.put(n.inputs[0], a)
        if b is not None:
            self.put(n.inputs[1], b)
        if scale is not None:
            self.put(n.inputs[3], scale)
        return n.outputs[out]

    def mix(self, kind, f, a, b, blend=None):
        n = self.new('ShaderNodeMix', data_type=kind)
        if blend:
            n.blend_type = blend
        suffix = {'FLOAT': 'Float', 'VECTOR': 'Vector', 'RGBA': 'Color'}[kind]
        ins = {s.identifier: s for s in n.inputs}
        self.put(ins['Factor_Float'], f)
        self.put(ins['A_' + suffix], a)
        self.put(ins['B_' + suffix], b)
        return {s.identifier: s for s in n.outputs}['Result_' + suffix]


SEASON_TONES = dict(autumn=((1.3, 1.05, 0.6), 0.55), winter=((1.6, 1.4, 1.0), 0.85))


def preview_material(name, chains, entry, k_pix, season='summer'):
    """Как NM_Foliage (Nature Renderer): альбедо × lerp(dry, healthy, шум), пора года (SeasonFoliage.hlsl, яркость
    0.299/0.587/0.114), альфа-тест, мип по расстоянию и углу (анизотропия 4), нормаль карты в базисе вершин, свет с обеих
    сторон (диффуз + просвечивание)."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    N = Nodes(mat)
    out = N.new('ShaderNodeOutputMaterial')
    uv = N.new('ShaderNodeUVMap')
    alb_imgs, nrm_imgs = chains
    levels = len(alb_imgs)
    ta, tn = [], []
    for i in range(levels):
        a = N.new('ShaderNodeTexImage', image=alb_imgs[i], interpolation='Linear', extension='EXTEND')
        n = N.new('ShaderNodeTexImage', image=nrm_imgs[i], interpolation='Linear', extension='EXTEND')
        N.link(uv.outputs['UV'], a.inputs['Vector'])
        N.link(uv.outputs['UV'], n.inputs['Vector'])
        ta.append(a)
        tn.append(n)
    cam = N.new('ShaderNodeCameraData')
    geo = N.new('ShaderNodeNewGeometry')
    texel = entry.get('texel', 0.003)
    footprint = N.math('MULTIPLY', cam.outputs['View Distance'], k_pix / texel)
    lod0 = N.math('LOGARITHM', N.math('MAXIMUM', footprint, 1e-4), 2.0)
    cosv = N.math('ABSOLUTE', N.vmath('DOT_PRODUCT', geo.outputs['Incoming'], geo.outputs['True Normal'], out=1))
    aniso = N.math('MAXIMUM', N.math('MULTIPLY', N.math('LOGARITHM', N.math('MAXIMUM', N.math('MULTIPLY', cosv, 4.0), 1e-3),
                                                         2.0), -1.0), 0.0)
    lod = N.math('ADD', lod0, aniso)
    colour = ta[-1].outputs['Color']
    alpha = ta[-1].outputs['Alpha']
    nrm = tn[-1].outputs['Color']
    ao = tn[-1].outputs['Alpha']
    for i in range(levels - 2, -1, -1):
        f = N.math('SUBTRACT', lod, float(i), clamp=True)
        colour = N.mix('RGBA', f, ta[i].outputs['Color'], colour)
        alpha = N.mix('FLOAT', f, ta[i].outputs['Alpha'], alpha)
        nrm = N.mix('VECTOR', f, tn[i].outputs['Color'], nrm)
        ao = N.mix('FLOAT', f, tn[i].outputs['Alpha'], ao)
    geo2 = N.new('ShaderNodeNewGeometry')
    noise = N.new('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = max(0.05, entry.get('colorNoiseSpread', 0.3))
    noise.inputs['Detail'].default_value = 2.0
    N.link(geo2.outputs['Position'], noise.inputs['Vector'])
    tint = N.mix('RGBA', noise.outputs['Fac'], tuple(entry.get('dryColor', [1, 1, 1])) + (1.0,),
                 tuple(entry.get('healthyColor', [1, 1, 1])) + (1.0,))
    base = N.mix('RGBA', 1.0, colour, tint, blend='MULTIPLY')
    keep = entry.get('seasonKeep', 0.0)
    if season != 'summer':
        lum = N.vmath('DOT_PRODUCT', base, (0.299, 0.587, 0.114), out=1)
        lum_c = N.new('ShaderNodeCombineXYZ')
        for s in range(3):
            N.link(lum, lum_c.inputs[s])
        if keep > 1.5:
            if season == 'winter':
                base = N.mix('RGBA', 0.5, base, N.vmath('MULTIPLY', lum_c.outputs[0], (0.95, 0.92, 0.86)))
        elif keep > 0.5:
            if season == 'winter':
                base = N.mix('RGBA', 1.0, base, (0.85, 0.85, 0.75, 1.0), blend='MULTIPLY')
        elif season == 'spring':
            base = N.mix('RGBA', 1.0, base, (1.0, 1.12, 0.85, 1.0), blend='MULTIPLY')
        else:
            tone, k = SEASON_TONES[season]
            base = N.mix('RGBA', k, base, N.vmath('MULTIPLY', lum_c.outputs[0], tone))
    ao_lo, ao_hi = entry.get('aoMin', 0.0), entry.get('aoMax', 1.0)
    ao_f = N.math('ADD', 0.6, N.math('MULTIPLY', N.math('ADD', ao_lo, N.math('MULTIPLY', ao, ao_hi - ao_lo)), 0.4))
    aoc = N.new('ShaderNodeCombineXYZ')
    for s in range(3):
        N.link(ao_f, aoc.inputs[s])
    base = N.mix('RGBA', 1.0, base, aoc.outputs[0], blend='MULTIPLY')
    s = entry.get('normalScale', 1.0)
    sep = N.new('ShaderNodeSeparateXYZ')
    mad = N.new('ShaderNodeVectorMath', operation='MULTIPLY_ADD')
    N.link(nrm, mad.inputs[0])
    mad.inputs[1].default_value = (2.0, 2.0, 2.0)
    mad.inputs[2].default_value = (-1.0, -1.0, -1.0)
    N.link(mad.outputs[0], sep.inputs[0])
    nx = N.math('MULTIPLY', sep.outputs[0], s)
    ny = N.math('MULTIPLY', sep.outputs[1], s)
    nz = N.math('ADD', 1.0 - min(1.0, s), N.math('MULTIPLY', sep.outputs[2], min(1.0, s)))
    a_n = N.new('ShaderNodeAttribute', attribute_name='nrm', attribute_type='GEOMETRY')
    a_t = N.new('ShaderNodeAttribute', attribute_name='tan', attribute_type='GEOMETRY')
    a_s = N.new('ShaderNodeAttribute', attribute_name='tsgn', attribute_type='GEOMETRY')
    bit = N.vmath('SCALE', N.vmath('CROSS_PRODUCT', a_n.outputs['Vector'], a_t.outputs['Vector']), scale=a_s.outputs['Fac'])
    n_obj = N.vmath('ADD', N.vmath('ADD', N.vmath('SCALE', a_t.outputs['Vector'], scale=nx), N.vmath('SCALE', bit, scale=ny)),
                    N.vmath('SCALE', a_n.outputs['Vector'], scale=nz))
    vt = N.new('ShaderNodeVectorTransform', vector_type='NORMAL', convert_from='OBJECT', convert_to='WORLD')
    N.link(n_obj, vt.inputs['Vector'])
    n_world = N.vmath('NORMALIZE', vt.outputs['Vector'])
    bsdf = N.new('ShaderNodeBsdfPrincipled')
    N.link(base, bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value = 1.0 - 0.5 * entry.get('smoothnessMax', 0.2)
    bsdf.inputs['Specular IOR Level'].default_value = 0.3
    N.link(n_world, bsdf.inputs['Normal'])
    back = N.new('ShaderNodeBsdfTranslucent')
    N.link(base, back.inputs['Color'])
    N.link(N.vmath('SCALE', n_world, scale=-1.0), back.inputs['Normal'])
    add = N.new('ShaderNodeAddShader')
    N.link(bsdf.outputs['BSDF'], add.inputs[0])
    N.link(back.outputs['BSDF'], add.inputs[1])
    keep_a = N.math('GREATER_THAN', alpha, entry.get('alphaCutoff', 0.5))
    transp = N.new('ShaderNodeBsdfTransparent')
    mixs = N.new('ShaderNodeMixShader')
    N.link(keep_a, mixs.inputs['Fac'])
    N.link(transp.outputs['BSDF'], mixs.inputs[1])
    N.link(add.outputs['Shader'], mixs.inputs[2])
    N.link(mixs.outputs['Shader'], out.inputs['Surface'])
    return mat


def ground_material(hex_colour, snow=False):
    m = bpy.data.materials.new('ground')
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes['Principled BSDF']
    bsdf.inputs['Roughness'].default_value = 0.95
    bsdf.inputs['Specular IOR Level'].default_value = 0.3
    base = Vector(eg.hex_rgb(hex_colour))
    tc = nt.nodes.new('ShaderNodeTexCoord')
    coarse = nt.nodes.new('ShaderNodeTexNoise')
    coarse.inputs['Scale'].default_value = 0.08
    coarse.inputs['Detail'].default_value = 6
    nt.links.new(tc.outputs['Object'], coarse.inputs['Vector'])
    ramp = nt.nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = 0.3
    ramp.color_ramp.elements[0].color = (*(base * (0.92 if snow else 0.75)), 1)
    ramp.color_ramp.elements[1].position = 0.75
    ramp.color_ramp.elements[1].color = (*(base * (1.03 if snow else 1.2)), 1)
    nt.links.new(coarse.outputs['Fac'], ramp.inputs['Fac'])
    fine = nt.nodes.new('ShaderNodeTexNoise')
    fine.inputs['Scale'].default_value = 3.0
    fine.inputs['Detail'].default_value = 4
    nt.links.new(tc.outputs['Object'], fine.inputs['Vector'])
    m1 = nt.nodes.new('ShaderNodeMix')
    m1.data_type = 'RGBA'
    m1.blend_type = 'MULTIPLY'
    ins = {s.identifier: s for s in m1.inputs}
    ins['Factor_Float'].default_value = 0.15 if snow else 0.4
    nt.links.new(ramp.outputs['Color'], ins['A_Color'])
    nt.links.new(fine.outputs['Color'], ins['B_Color'])
    gain = nt.nodes.new('ShaderNodeMix')
    gain.data_type = 'RGBA'
    gain.blend_type = 'MULTIPLY'
    ins3 = {s.identifier: s for s in gain.inputs}
    ins3['Factor_Float'].default_value = 1.0
    ins3['B_Color'].default_value = (1.2, 1.2, 1.2, 1)
    nt.links.new({s.identifier: s for s in m1.outputs}['Result_Color'], ins3['A_Color'])
    nt.links.new({s.identifier: s for s in gain.outputs}['Result_Color'], bsdf.inputs['Base Color'])
    return m


class ValueNoise:
    def __init__(self, seed, n=128):
        self.g = np.random.default_rng(seed).random((n, n))
        self.n = n

    def __call__(self, x, y):
        ix, iy = math.floor(x), math.floor(y)
        fx, fy = x - ix, y - iy
        fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
        g, n = self.g, self.n
        a, b = g[ix % n, iy % n], g[(ix + 1) % n, iy % n]
        c, d = g[ix % n, (iy + 1) % n], g[(ix + 1) % n, (iy + 1) % n]
        return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy

    def fbm(self, x, y):
        return (self(x, y) + 0.5 * self(x * 2 + 17.3, y * 2 + 5.1) + 0.25 * self(x * 4 + 3.7, y * 4 + 11.9)) / 1.75


LIGHT = dict(sun_col=(1.0, 0.91, 0.79), sun_e=1.72, sky_col=(0.86, 0.93, 1.0), sky=0.27)
VIEWS = {
    # кадр как у FPV-камеры (вертикальный угол 70°): сверху с 30 м; наискось с 15 м, взгляд вперёд-вниз на 40°;
    # вблизи — с 1.6 м, 3 м до края заросли
    'top': dict(loc=(0.0, 0.0, 30.0), target=(0.0, 0.01, 0.0), clear=None),
    'oblique': dict(loc=(0.0, -24.0, 15.0), target=(0.0, -6.0, 0.0), clear=None),
    'close': dict(loc=(0.0, -3.2, 1.6), target=(0.0, 1.5, 0.55), clear=-0.2),
}


def game_hash01(x, z, seed):
    """GroundDressing.Hash01 (uint32 как в C#)."""
    h = (x * 374761393 + z * 668265263 + seed * 1442695041) & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    h ^= h >> 16
    return (h & 0x00FFFFFF) / 16777216.0


def game_clump(x, z, scale, strength, seed):
    """Множитель куртин записи профиля, как в GroundDressing: lerp(1, smoothstep(0.3, 0.8, Contrasted) × 2, strength)."""
    if strength <= 0:
        return 1.0
    fx, fz = x / scale, z / scale
    x0, z0 = math.floor(fx), math.floor(fz)
    tx, tz = fx - x0, fz - z0
    tx, tz = tx * tx * (3 - 2 * tx), tz * tz * (3 - 2 * tz)
    s = seed + 1
    bottom = lerp(game_hash01(x0, z0, s), game_hash01(x0 + 1, z0, s), tx)
    top = lerp(game_hash01(x0, z0 + 1, s), game_hash01(x0 + 1, z0 + 1, s), tx)
    v = min(1.0, max(0.0, (lerp(bottom, top, tz) - 0.5) * 1.8 + 0.5))
    return lerp(1.0, float(smoothstep(0.3, 0.8, v)) * 2.0, strength)


SEASON_INDEX = dict(spring=0, summer=1, autumn=2, winter=3)


def species_entries(species, density, season):
    """Поле вида: все его варианты (папки, что есть), плотность поровну и × SeasonScales подсказки; зимой — только
    AboveSnow (остальное под снегом)."""
    spec = ASSETS[species]
    hint = spec['hint']
    if season == 'winter' and not hint['aboveSnow']:
        return []
    k = hint['seasons'][SEASON_INDEX[season]]
    names = [species + v['suffix'] for v in spec['variants']]
    return [(n, density * k / len(names), species) for n in names]


def preview(args):
    p = argparse.ArgumentParser()
    p.add_argument('--out', default=DEFAULT_OUT)
    p.add_argument('--assets', nargs='*', default=None)
    p.add_argument('--mix', default=None, help='вид:экз/м²,… — поле из нескольких видов (лист <out>/_preview/<tag>_*)')
    p.add_argument('--tag', default=None)
    p.add_argument('--views', default='top,oblique,close')
    p.add_argument('--res', default='1280x720')
    p.add_argument('--samples', type=int, default=32)
    p.add_argument('--season', default='summer', choices=['summer', 'autumn', 'winter', 'spring'])
    p.add_argument('--ground', default=None)
    p.add_argument('--density', type=float, default=None)
    p.add_argument('--clump', type=float, default=None, help='сила куртин для всех (по умолчанию — из подсказок)')
    p.add_argument('--seed', type=int, default=5)
    p.add_argument('--vfov', type=float, default=70.0)
    p.add_argument('--reach', type=float, default=110.0)
    p.add_argument('--prev', default=None, help='куда писать листы (по умолчанию <out>/_preview)')
    a = p.parse_args(args)
    out_dir = a.out
    prev_dir = a.prev or os.path.join(out_dir, '_preview')
    os.makedirs(prev_dir, exist_ok=True)
    if a.mix:
        entries = []
        for part in a.mix.split(','):
            name, dens = part.rsplit(':', 1)
            entries += species_entries(name, float(dens), a.season)
        jobs = [(a.tag or 'mix', entries)]
    else:
        names = a.assets or list(ASSETS)
        # густота — верх подсказки профиля (сплошная заросль; куртины по шуму оставляют прогалы)
        jobs = [(n, species_entries(n, a.density or float(ASSETS[n]['hint']['density'][1]), a.season)) for n in names]
    res_x, res_y = (int(v) for v in a.res.split('x'))
    snow = a.season == 'winter'
    ground = a.ground or ('#DDE2E6' if snow else '#7A7158')
    for tag, entries_spec in jobs:
        entries_spec = [e for e in entries_spec if os.path.exists(os.path.join(out_dir, e[0], 'manifest.json'))]
        if not entries_spec:
            print('SKIP', tag, 'nothing to place', flush=True)
            continue
        paths, coverage = [], {}
        for view in a.views.split(','):
            path = os.path.join(prev_dir, f'{tag}_{a.season}_{view}.png' if a.season != 'summer' else f'{tag}_{view}.png')
            bare = render_view(out_dir, entries_spec, VIEWS[view], path, res_x, res_y, a, ground, snow,
                               measure=(view == 'top'))
            if bare:
                coverage[view] = bare
            paths.append(path)
        sheet = os.path.join(prev_dir, f'{tag}_{a.season}_sheet.png' if a.season != 'summer' else f'{tag}_sheet.png')
        compose_sheet(paths, sheet)
        if coverage:
            cov_path = sheet.replace('_sheet.png', '_coverage.json')
            with open(cov_path, 'w', encoding='utf-8') as f:
                json.dump(dict(entries=[dict(prefab=n, density=round(d, 4)) for n, d, _ in entries_spec], **coverage), f,
                          indent=1)
            print('COVERAGE', tag, a.season, json.dumps(coverage), flush=True)
        print('SHEET_OK', sheet, flush=True)


def holdout_alpha(sc, ground_obj, res_x, res_y, path):
    """Доля голой земли в кадре: земля — holdout, кадр с прозрачным фоном (4 сэмпла); альфа, строки сверху вниз."""
    hold = bpy.data.materials.new('holdout')
    hold.use_nodes = True
    nt = hold.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    nt.links.new(nt.nodes.new('ShaderNodeHoldout').outputs[0], out.inputs['Surface'])
    ground_obj.data.materials.clear()
    ground_obj.data.materials.append(hold)
    sc.render.film_transparent = True
    sc.cycles.samples = 4
    sc.cycles.use_denoising = False
    sc.render.image_settings.color_mode = 'RGBA'
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(path, check_existing=False)
    a = np.array(img.pixels[:], np.float32).reshape(res_y, res_x, 4)[::-1, :, 3].copy()
    bpy.data.images.remove(img)
    return a


def render_view(out_dir, entries_spec, view, path, res_x, res_y, a, ground, snow, measure=False):
    sc = eg.reset_scene()
    bpy.ops.mesh.primitive_plane_add(size=4000)
    ground_obj = bpy.context.active_object
    ground_obj.data.materials.append(ground_material(ground, snow))
    cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam'))
    sc.collection.objects.link(cam)
    sc.camera = cam
    cam.data.sensor_fit = 'VERTICAL'
    cam.data.angle_y = math.radians(a.vfov)
    cam.data.clip_start = 0.05
    cam.data.clip_end = 3000
    cam.location = view['loc']
    cam.rotation_euler = (Vector(view['target']) - Vector(view['loc'])).to_track_quat('-Z', 'Y').to_euler()
    bpy.context.view_layer.update()
    cam_inv = cam.matrix_world.inverted()
    tan_y = math.tan(math.radians(a.vfov) / 2)
    tan_x = tan_y * res_x / res_y
    k_pix = 2 * tan_y / res_y
    cam_pos = Vector(view['loc'])
    entries = []
    materials = {}   # варианты ассета — один атлас и один материал (как в Unity)
    for i, (name, density, species) in enumerate(entries_spec):
        folder = os.path.join(out_dir, name)
        with open(os.path.join(folder, 'manifest.json'), encoding='utf-8') as f:
            manifest = json.load(f)
        entry = manifest['materials'][0]
        if entry['id'] not in materials:
            cutoff = entry.get('alphaCutoff', 0.5)
            alb = load_png(os.path.join(folder, entry['albedo']), True)
            nrm = load_png(os.path.join(folder, entry['normal']), False)
            nrm[..., 3] = load_png(os.path.join(folder, entry['mask']), False)[..., 1]
            amips = unity_mips(alb, cutoff, 7)
            nmips = unity_mips(nrm, None, 7)
            j = len(materials)
            chains = ([make_image(f'a{j}_{k}', m, srgb=True) for k, m in enumerate(amips)],
                      [make_image(f'n{j}_{k}', m) for k, m in enumerate(nmips)])
            materials[entry['id']] = preview_material('m_' + entry['id'], chains, entry, k_pix, a.season)
        mat = materials[entry['id']]
        bpy.ops.import_scene.fbx(filepath=os.path.join(folder, manifest['lods'][0]['fbx']))
        o = bpy.context.selected_objects[0]
        # поворот импорта (Y вверх FBX) — в меш: копии потом крутятся только вокруг Z
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
        me = o.data
        me.materials.clear()
        me.materials.append(mat)
        me.calc_tangents(uvmap=me.uv_layers[0].name)
        an = me.attributes.new('nrm', 'FLOAT_VECTOR', 'CORNER')
        at = me.attributes.new('tan', 'FLOAT_VECTOR', 'CORNER')
        asg = me.attributes.new('tsgn', 'FLOAT', 'CORNER')
        cn = me.corner_normals
        for li, loop in enumerate(me.loops):
            an.data[li].vector = cn[li].vector
            at.data[li].vector = loop.tangent
            asg.data[li].value = loop.bitangent_sign
        o.location = (0, 0, -100)
        nr = manifest.get('nr') or {}
        hint = ASSETS[species]['hint'] if species in ASSETS else {}
        clump = (14.0, a.clump) if a.clump is not None else tuple(hint.get('clump', (14.0, 0.0)))
        entries.append(dict(obj=o, density=density, sv=entry.get('sizeVariation', 0.0), name=name,
                            dist=min(a.reach, nr.get('renderDistance', 100.0)), shadows=nr.get('shadows', False),
                            clump=clump, seed=7919 * (i + 1) + a.seed, radius=max(manifest['size'][:2]) * 0.5))
    size_noise = ValueNoise(91)
    rng = random.Random(a.seed)
    placed = 0

    def visible(x, y, r=1.0):
        pc = cam_inv @ Vector((x, y, 0.5))
        depth = -pc.z
        if depth < -r:
            return False
        dd = max(depth, 0.05)
        return abs(pc.x) < dd * tan_x + r * 1.5 and abs(pc.y) < dd * tan_y + r * 1.5

    cell = 2.0
    reach = max(e['dist'] for e in entries)
    cx, cy = view['loc'][0], view['loc'][1]
    for ix in range(int(math.floor((cx - reach) / cell)), int(math.ceil((cx + reach) / cell))):
        for iy in range(int(math.floor((cy - reach) / cell)), int(math.ceil((cy + reach) / cell))):
            for e in entries:
                # куртины записи — как GroundDressing (ClumpScale/ClumpStrength подсказки, свой seed у записи)
                k = game_clump((ix + 0.5) * cell, (iy + 0.5) * cell, e['clump'][0], e['clump'][1], e['seed'])
                expected = e['density'] * k * cell * cell
                n = int(expected) + (1 if rng.random() < expected - int(expected) else 0)
                for _ in range(n):
                    x, y = (ix + rng.random()) * cell, (iy + rng.random()) * cell
                    yaw = rng.uniform(0, 2 * math.pi)
                    own = rng.random()
                    if view['clear'] is not None and y < view['clear']:
                        continue
                    d = (Vector((x, y, 0)) - cam_pos).length
                    if d > e['dist'] or not visible(x, y, e['radius']):
                        continue
                    smaller = min(1.0, max(0.0, size_noise(x / 6 + 91.7, y / 6 + 91.7) * 0.6 + own * 0.4))
                    s = 1.0 - e['sv'] * smaller * math.sqrt(smaller)
                    o = e['obj'].copy()
                    sc.collection.objects.link(o)
                    o.location = (x, y, 0)
                    o.rotation_euler = (0, 0, yaw)
                    o.scale = (s, s, s)
                    o.visible_shadow = bool(e['shadows'] and d < 20.0)
                    o.visible_diffuse = False
                    o.visible_glossy = False
                    o.visible_transmission = False
                    placed += 1
    sun = bpy.data.lights.new('sun', 'SUN')
    sun.energy = LIGHT['sun_e']
    sun.angle = math.radians(1.0)
    sun.color = LIGHT['sun_col']
    so = bpy.data.objects.new('sun', sun)
    sc.collection.objects.link(so)
    so.rotation_euler = (math.radians(50), 0, math.radians(35))
    world = bpy.data.worlds.new('w')
    sc.world = world
    world.use_nodes = True
    wnt = world.node_tree
    bg = wnt.nodes['Background']
    bg.inputs['Color'].default_value = LIGHT['sky_col'] + (1.0,)
    bg.inputs['Strength'].default_value = LIGHT['sky']
    seen = wnt.nodes.new('ShaderNodeBackground')
    seen.inputs['Color'].default_value = (0.78, 0.82, 0.88, 1.0)
    lp = wnt.nodes.new('ShaderNodeLightPath')
    mixw = wnt.nodes.new('ShaderNodeMixShader')
    wnt.links.new(lp.outputs['Is Camera Ray'], mixw.inputs['Fac'])
    wnt.links.new(bg.outputs['Background'], mixw.inputs[1])
    wnt.links.new(seen.outputs['Background'], mixw.inputs[2])
    wnt.links.new(mixw.outputs['Shader'], wnt.nodes['World Output'].inputs['Surface'])
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = a.samples
    use_device(sc)
    sc.cycles.use_denoising = True
    sc.cycles.max_bounces = 3
    sc.cycles.diffuse_bounces = 2
    sc.cycles.transparent_max_bounces = 256
    sc.render.filter_size = 0.75
    sc.render.resolution_x = res_x
    sc.render.resolution_y = res_y
    sc.render.resolution_percentage = 100
    sc.view_settings.view_transform = 'Standard'
    sc.view_settings.look = 'None'
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGB'
    sc.render.filepath = path
    t0 = time.time()
    bpy.ops.render.render(write_still=True)
    print('VIEW_OK', path, 'instances', placed, 'render_s', round(time.time() - t0, 1), flush=True)
    if not measure:
        return None
    # голая земля сверху: в середине кадра (квадрат 0.4 высоты кадра — у надира, ±8 м с 30 м) и во всём кадре
    os.makedirs(CACHE_DIR, exist_ok=True)
    alpha = holdout_alpha(sc, ground_obj, res_x, res_y, os.path.join(CACHE_DIR, 'holdout.png'))
    hh = int(res_y * 0.2)
    crop = alpha[res_y // 2 - hh:res_y // 2 + hh, res_x // 2 - hh:res_x // 2 + hh]
    return dict(bare_nadir=round(float((crop < 0.5).mean()), 3), bare_frame=round(float((alpha < 0.5).mean()), 3),
                instances=placed)



def compose_sheet(paths, out_path, gap=6):
    imgs = []
    for pth in paths:
        img = bpy.data.images.load(pth, check_existing=False)
        w, h = img.size
        imgs.append(np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1].copy())
        bpy.data.images.remove(img)
    h = max(i.shape[0] for i in imgs)
    w = sum(i.shape[1] for i in imgs) + gap * (len(imgs) - 1)
    sheet = np.ones((h, w, 4), np.float32) * np.array([0.12, 0.12, 0.12, 1.0], np.float32)
    x = 0
    for i in imgs:
        sheet[:i.shape[0], x:x + i.shape[1]] = i
        x += i.shape[1] + gap
    eg.save_png(sheet, out_path, linear=True)


# ============================================================================================== растения по одному
def plants_preview(args):
    """plants <out.png> <вид>[:параметр=знач,…] … — растения в ряд, вид сбоку (эмиссия цвета вершин на сером)."""
    texel = 0.004
    if '--texel' in args:
        i = args.index('--texel')
        texel = float(args[i + 1])
        args = args[:i] + args[i + 2:]
    out = args[0]
    eg.reset_scene()
    x = 0.0
    objs = []
    for k, spec in enumerate(args[1:]):
        kind, _, rest = spec.partition(':')
        params = {}
        for kv in filter(None, rest.split(',')):
            key, val = kv.split('=')
            params[key] = val if key == 'kind' else (val == 'True' if val in ('True', 'False') else float(val))
        rng = random.Random(1000 + k)
        g = PLANTS[kind](rng, **params)
        lo, hi = g.bounds()
        ob = g.to_object(f'p{k}')
        ob.location = (x - lo[0], 0, 0)
        x += (hi[0] - lo[0]) + 0.05
        objs.append(ob)
        print('PLANT', kind, params, 'faces', g.faces, 'size', [round(float(v), 2) for v in hi - lo], flush=True)
    sc = bpy.context.scene
    hmax = max(o.dimensions.z for o in objs) + 0.05
    eg.ortho_camera(sc, (x * 0.5, -3, hmax * 0.5), (math.radians(90), 0, 0), max(x, hmax * 2))
    world = bpy.data.worlds.new('w')
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Color'].default_value = (0.5, 0.5, 0.5, 1)
    sc.world = world
    mat = pass_material('albedo')
    for o in objs:
        o.data.materials.append(mat)
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = 16
    use_device(sc)
    width = max(x, hmax * 2)
    sc.render.resolution_x = int(width / texel)
    sc.render.resolution_y = int(hmax / texel)
    sc.view_settings.view_transform = 'Standard'
    sc.render.filepath = out
    bpy.ops.render.render(write_still=True)
    print('PLANTS_OK', out)


# ============================================================================================== запуск
def main():
    global DEVICE
    args = eg.script_args()
    if not args:
        print('usage: ruderal_gen.py -- all|atlas|assets|preview|plants ... (see the header)')
        return
    cmd, rest = args[0], args[1:]
    if '--device' in rest:
        i = rest.index('--device')
        DEVICE = rest[i + 1].upper()
        rest = rest[:i] + rest[i + 2:]
    if cmd == 'plants':
        plants_preview(rest)
        return
    if cmd == 'preview':
        preview(rest)
        return
    p = argparse.ArgumentParser()
    p.add_argument('--out', default=DEFAULT_OUT)
    p.add_argument('--cache', default=CACHE_DIR)
    p.add_argument('--assets', nargs='*', default=None)
    p.add_argument('--regions', nargs='*', default=None)
    a = p.parse_args(rest)
    names = a.assets or list(ASSETS)
    os.makedirs(a.out, exist_ok=True)
    t0 = time.time()
    if cmd in ('atlas', 'all'):
        for name in names:
            compose_atlas(name, ASSETS[name], a.out, a.regions, a.cache)
    if cmd in ('assets', 'all'):
        build_assets(a.out, names)
    print('DONE', cmd, round(time.time() - t0, 1), 's', flush=True)


main()
