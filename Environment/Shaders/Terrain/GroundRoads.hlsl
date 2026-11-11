// Покрытие дорог на террейне (Game/Terrain Lit (Wet), из GroundWetness.hlsl). Карта дорог вокруг камеры (RoadMap →
// _RoadMap, _RoadMapInfo, Hidden/Game/Road Map) хранит в каждом пикселе у дороги её отрезок; по отрезкам четырёх
// соседних пикселей здесь точно считаются метры от оси (+ слева по ходу), метры вдоль дороги, и берутся полуширина, вид
// покрытия, разметка, износ и бордюр; какая из дорог в точке главная и как у неё кончается полотно — RoadMapCommon.hlsl
// (так же, как решала карта). По ним — полотно в осях дороги:
//  - асфальт: полоса фактуры GeoWorld поперёк дороги (GeoRoadTextures: от кромки до кромки, 12 м вдоль, тайлится; четыре
//    варианта от почти нового до разбитого — колеи, заплаты с тёмными швами, трещины, сетка трещин, выбоины, пыль с
//    обочин) — вариант по износу дороги, каждые 12 м фактура то прямо, то зеркально вдоль (шов незаметен, повтор не
//    читается), каждые 36 м вариант может смениться на соседний — на стыке плавно; тон плывёт участками ремонта. В городе
//    (бордюр) — ровная кромка, светлый бетонный бордюр с тёмной щелью у полотна; в селе — кромка рваная по износу,
//    гравийная обочина. У размеченных — ось штрихами (3 м / 9 м), края сплошные (у бордюра краёв нет);
//  - грунтовка (полевая, лесная): две колеи (~0.5 м, ~1.6 м между центрами, чуть виляют, вдавлены), в сухую погоду
//    пыльный светлый грунт, колея светлее всего, в сырость — тёмная грязь; трава между колеями и по краям остаётся (тем
//    меньше, чем дорога изношеннее); узкая — одна тропа;
//  - гравийка: серо-бежевый щебень на всю ширину, по колеям укатан;
//  - плитка — бетонная плитка; тропинка — вытоптанная полоса; рельсы — щебень насыпи, шпалы, рельсы.
// На перекрёстке (полотна разных дорог накрывают друг друга, _RoadJunction) кромок между ними нет: грунт — голая
// укатанная площадка, гравий и плитка — сплошные до стыка с другой дорогой, у асфальта ни бордюра, ни обочины, ни разметки,
// у рельсов переездный настил между рельсами.
// hollow — где на полотне стоит вода в дождь (колеи грунтовок и гравийки, у асфальта — вдоль кромки и выбоины): из неё
// GroundWetness набирает лужи. Мелочь (колеи, разметка, шпалы), которую пиксель накрывает целиком, бледнеет до своей
// средней доли — вдали не мельтешит. Своих сэмплеров нет: карта — LOAD, фактуры — массивы
// сэмплером снега (повтор, трилинейный). Нужны до этого файла: Puddles.hlsl (шум), GroundDetail.hlsl (массивы фактуры
// земли), Snow.hlsl (сэмплер), _GroundWetness.
#ifndef GAME_GROUND_ROADS_INCLUDED
#define GAME_GROUND_ROADS_INCLUDED

#include "RoadMapCommon.hlsl"

// карта дорог (RoadMap): в каждом пикселе у дороги — её отрезок (xy — A, zw — B, мир xz) и его данные (x — полуширина,
// y — метры от начала дороги до A, z — вид покрытия + 8 × разметка + 16 × износ + 128 × бордюр + 256 × начало + 1024 ×
// конец дороги, w — метры от A до конца дороги)
TEXTURE2D(_RoadMap);
TEXTURE2D(_RoadMapInfo);
// перекрёстки: R — меньший номер дороги, накрывающей точку полотном с бордюром, A — больший (разные — перекрёсток)
TEXTURE2D(_RoadJunction);
// xy — начало окна в мире, z — 1/размер окна, w — текселей по стороне (0 — карты нет)
float4 _RoadMapRect;
// дальнее окно (RoadMap, до дальности загрузки тайлов; тексель в несколько метров) — то же, за ближним окном
TEXTURE2D(_RoadMapFar);
TEXTURE2D(_RoadMapInfoFar);
TEXTURE2D(_RoadJunctionFar);
float4 _RoadMapRectFar;
// слои массивов фактуры земли: x асфальт, y гравий, z грязь, w плитка
float4 _RoadLayers;
// x — сухой укатанный грунт, y — сухая трава
float4 _RoadLayers2;
// полосы асфальта GeoWorld (RoadSurfaceSet): x — длина вдоль дороги (м), y — вариантов, z — есть ли
TEXTURE2D_ARRAY(_RoadAsphaltAlbedo);
TEXTURE2D_ARRAY(_RoadAsphaltNormal);
float4 _RoadAsphalt;

#define ROAD_DIRT 0
#define ROAD_ASPHALT 1
#define ROAD_PAVING 2
#define ROAD_GRAVEL 3
#define ROAD_PATH 4
#define ROAD_RAIL 5

struct RoadPoint
{
    // метры от оси (+ слева по ходу) и от начала дороги
    float across;
    float along;
    // полуширина полотна; 0 — дороги рядом нет
    float halfWidth;
    int kind;
    bool marked;
    bool curb;
    half wear;
    // у края окна карты сходит на нет
    half window;
    // у тупика полотно сходит на нет
    half fade;
    // перекрёсток (полотна разных дорог накрывают друг друга): ни бордюров, ни обочин, ни разметки, асфальт сплошной
    half junction;
    // куда растут across и along (мир xz)
    float2 acrossDirection;
    float2 alongDirection;
};

float4 LoadRoadSegment(bool far, int2 c)
{
    [branch] if (far)
        return LOAD_TEXTURE2D(_RoadMapFar, c);
    return LOAD_TEXTURE2D(_RoadMap, c);
}

float4 LoadRoadInfo(bool far, int2 c)
{
    [branch] if (far)
        return LOAD_TEXTURE2D(_RoadMapInfoFar, c);
    return LOAD_TEXTURE2D(_RoadMapInfo, c);
}

float4 LoadRoadJunction(bool far, int2 c)
{
    [branch] if (far)
        return LOAD_TEXTURE2D(_RoadJunctionFar, c);
    return LOAD_TEXTURE2D(_RoadJunction, c);
}

// Дорога в точке по одному окну карты (rect — его _RoadMapRect…); fadeEdge — сходить на нет у края окна (у ближнего, когда
// за ним нет дальнего, и у дальнего).
RoadPoint SampleRoadMap(float2 xz, float4 rect, bool far, bool fadeEdge)
{
    RoadPoint road = (RoadPoint)0;
    road.acrossDirection = float2(1, 0);
    road.alongDirection = float2(0, -1);

    [branch] if (rect.w > 0.5)
    {
        float2 uv = (xz - rect.xy) * rect.z;
        float2 edge = min(uv, 1 - uv);
        road.window = fadeEdge ? saturate(min(edge.x, edge.y) * 12) : (min(edge.x, edge.y) > 0 ? 1 : 0);

        if (road.window > 0)
        {
            // отрезки четырёх соседних пикселей карты: у каждого — точное расстояние до него в этой точке, берём главную
            // дорогу (как карта выбирала в пикселе). Ничего не смешивается — кромки, бордюры и колеи точные
            int size = (int)rect.w;
            float2 cell = uv * size - 0.5;
            int2 c0 = clamp((int2)floor(cell), 0, size - 2);
            float bestKey = 1e9;
            float4 bestSegment = 0;
            float4 bestInfo = 0;
            RoadMeasure best = (RoadMeasure)0;

            [unroll] for (int k = 0; k < 4; k++)
            {
                int2 c = c0 + int2(k & 1, k >> 1);
                float4 info = LoadRoadInfo(far, c);

                [branch] if (info.x > 0.01)
                {
                    float4 segment = LoadRoadSegment(far, c);
                    RoadMeasure m = MeasureRoad(xz, segment.xy, segment.zw, info);

                    if (m.valid && m.key < bestKey)
                    {
                        bestKey = m.key;
                        best = m;
                        bestSegment = segment;
                        bestInfo = info;
                    }
                }
            }

            [branch] if (bestInfo.x > 0.01)
            {
                float2 ab = bestSegment.zw - bestSegment.xy;
                float2 direction = ab * rsqrt(max(dot(ab, ab), 1e-6));
                road.across = best.across;
                road.along = bestInfo.y + best.s;
                road.halfWidth = bestInfo.x;
                road.fade = best.fade;
                float packed = fmod(round(bestInfo.z), 256);
                road.kind = (int)fmod(packed, 8);
                road.marked = fmod(floor(packed / 8), 2) > 0.5;
                road.wear = fmod(floor(packed / 16), 8) / 7.0;
                road.curb = floor(packed / 128) > 0.5;
                // поперёк — налево по ходу (туда растёт across), вдоль — по ходу
                road.acrossDirection = float2(-direction.y, direction.x);
                road.alongDirection = direction;

                // перекрёсток — по четырём соседним текселям (номера дорог не смешиваются, смешивается признак)
                float2 f = saturate(cell - c0);
                float4 j00 = LoadRoadJunction(far, c0);
                float4 j10 = LoadRoadJunction(far, c0 + int2(1, 0));
                float4 j01 = LoadRoadJunction(far, c0 + int2(0, 1));
                float4 j11 = LoadRoadJunction(far, c0 + int2(1, 1));
                half4 flags = half4(j00.a - j00.r > 0.5, j10.a - j10.r > 0.5, j01.a - j01.r > 0.5, j11.a - j11.r > 0.5);
                road.junction = lerp(lerp(flags.x, flags.y, f.x), lerp(flags.z, flags.w, f.x), f.y);
            }
        }
    }

    return road;
}

// Внутри ближнего окна — оно (1 м на тексель), за ним — дальнее. В обоих окнах одни и те же отрезки и мерит террейн до них
// одинаково точно, поэтому на стыке окон дорога не меняется и ближнее окно у края не гаснет, если дальнее есть.
RoadPoint SampleRoad(float2 xz)
{
    bool hasFar = _RoadMapRectFar.w > 0.5;
    float2 nearUV = (xz - _RoadMapRect.xy) * _RoadMapRect.z;
    bool inNear = _RoadMapRect.w > 0.5 && all(nearUV > 0) && all(nearUV < 1);

    [branch] if (inNear || !hasFar)
        return SampleRoadMap(xz, _RoadMapRect, false, !hasFar);

    return SampleRoadMap(xz, _RoadMapRectFar, true, true);
}

// Фактура в точке — со своим цветом (не отношением к среднему, как у GroundDetail); нормаль — в осях мира x, z.
struct RoadTexel
{
    half3 colour;
    half3 normalTS;
    half ao;
    half smoothness;
};

RoadTexel RoadTexture(float layer, float2 xz, float2 dpdx, float2 dpdy)
{
    float4 settings = _GroundDetailLayer[(uint)layer];
    float2 uv = xz * settings.x;
    float2 dx = dpdx * settings.x;
    float2 dy = dpdy * settings.x;
    half4 albedo = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailAlbedo, sampler_SnowAlbedo, uv, layer, dx, dy);
    half4 packed = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailNormal, sampler_SnowAlbedo, uv, layer, dx, dy);
    half4 mask = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailMask, sampler_SnowAlbedo, uv, layer, dx, dy);

    RoadTexel texel;
    texel.colour = albedo.rgb;
    // BC5 (x в R) и DXT5nm (x в A, R = 1)
    half2 xy = (half2(packed.r * packed.a, packed.g) * 2 - 1) * settings.z;
    texel.normalTS = half3(xy, sqrt(saturate(1 - dot(xy, xy))));
    texel.ao = lerp(1, mask.g, settings.w);
    texel.smoothness = mask.b;
    return texel;
}

// Цвет фактуры в своём тоне: рисунок (отношение к среднему цвету слоя) на заданный средний цвет. У фактур набора свои
// тона (гравий Poly Haven — рыжий, сухой грунт — тёплый коричневый), а дороги здесь — серый щебень и пыльный грунт.
half3 RoadTone(RoadTexel texel, float layer, half3 tone)
{
    return texel.colour / max(_GroundDetailAverage[(uint)layer].rgb, 0.005) * tone;
}

RoadTexel RoadMix(RoadTexel a, RoadTexel b, half t)
{
    RoadTexel m;
    m.colour = lerp(a.colour, b.colour, t);
    m.normalTS = normalize(lerp(a.normalTS, b.normalTS, t));
    m.ao = lerp(a.ao, b.ao, t);
    m.smoothness = lerp(a.smoothness, b.smoothness, t);
    return m;
}

// Полоса асфальта GeoWorld: u — от левой кромки до правой, v — вдоль (доли длины полосы); нормаль из осей полосы
// (x — поперёк, y — вдоль) — в оси мира.
RoadTexel AsphaltStrip(float2 uv, float variant, float2 uvdx, float2 uvdy, RoadPoint r, float mirror)
{
    half4 albedo = SAMPLE_TEXTURE2D_ARRAY_GRAD(_RoadAsphaltAlbedo, sampler_SnowAlbedo, uv, variant, uvdx, uvdy);
    half4 packed = SAMPLE_TEXTURE2D_ARRAY_GRAD(_RoadAsphaltNormal, sampler_SnowAlbedo, uv, variant, uvdx, uvdy);
    half2 strip = half2(packed.r * packed.a, packed.g) * 2 - 1;
    half2 world = strip.x * r.acrossDirection + strip.y * mirror * r.alongDirection;

    RoadTexel texel;
    texel.colour = albedo.rgb;
    texel.normalTS = half3(world, sqrt(saturate(1 - dot(strip, strip))));
    texel.ao = 1;
    texel.smoothness = 0.18;
    return texel;
}

// Полоса шириной 2·halfWidth вокруг центра (distance — до центра), мягкий край; где пиксель шире полосы — её средняя
// доля (average), а не мельтешение.
half RoadStripe(float distance, float halfWidth, float footprint, half average)
{
    half stripe = 1 - smoothstep(halfWidth * 0.6, halfWidth, distance);
    return lerp(stripe, average, saturate(footprint / max(halfWidth * 2, 1e-3) - 0.5));
}

// Наклон колеи (dh/dx поперёк дороги): профиль — вдавленная на depth полоса со склонами smoothstep вокруг offset.
float RutSlope(float across, float offset, float halfWidth, float depth)
{
    float ax = abs(across);
    float d = abs(ax - offset);
    float a = halfWidth * 0.5;
    float t = saturate((d - a) / max(halfWidth - a, 1e-3));
    float slope = depth * 6 * t * (1 - t) / max(halfWidth - a, 1e-3);
    return slope * sign(ax - offset) * sign(across);
}

float RoadHash(float2 cell, uint salt)
{
    uint2 id = (uint2)(int2)cell;
    return GenerateHashedRandomFloat(uint3(id.x, id.y, salt));
}

// Вариант полосы асфальта на участке block (36 м): по износу дороги, у соседних участков — свой разброс.
float AsphaltVariant(float block, half wear)
{
    float count = max(_RoadAsphalt.y, 1);
    return clamp(round(wear * (count - 1) + (RoadHash(float2(block, 3), 81u) - 0.5) * 1.3), 0, count - 1);
}

// Полотно дороги поверх земли. road — доля полотна (снег на нём укатан), hollow — где на нём собирается вода (лужи),
// paved — доля асфальта и плитки (их рвут воронки, GroundRoadScars). winter — дорога зимой (снег кладёт GroundWetness):
// x — проезжая часть любой дороги (снег на ней укатан во всю ширину, а не только по колеям), y — колеи колёс (в них
// снег разъезжен до земли), z — расчищенное полотно (асфальт и плитку чистят: тёмная мокрая лента), w — снежный вал
// за кромкой расчищенной дороги (снег, отброшенный плугом). С высоты дорога зимой — тёмная лента между белыми валами
// или серая укатанная полоса с тёмными колеями, а не снег поля.
void GroundRoad(inout InputData inputData, float2 dpdx, float2 dpdy, inout half3 albedo, inout half smoothness, inout half occlusion,
    out half road, out half hollow, out half paved, out half4 winter)
{
    road = 0;
    hollow = 0;
    paved = 0;
    winter = 0;
    half pavedShare = 0;
    float2 xz = inputData.positionWS.xz;
    RoadPoint r = SampleRoad(xz);

    [branch] if (r.halfWidth < 0.05 || abs(r.across) > r.halfWidth + 1.2)
        return;

    float footprint = max(length(dpdx), length(dpdy));
    float x = r.across;
    float ax = abs(x);
    float h = r.halfWidth;
    // рваный край: трава и обочина съедают кромку неровно
    float ragged = (PuddleNoise(xz / 1.9 + 7.7) - 0.5) * 0.45 + (PuddleNoise(xz / 0.55 + 3.1) - 0.5) * 0.2;
    // в сырость грунт — грязь
    half wet = saturate(_GroundWetness * 1.5);
    half greenness = (albedo.g - max(albedo.r, albedo.b)) / max(albedo.g, 0.02);
    half bareSatellite = 1 - smoothstep(-0.05, 0.15, greenness);

    half cover = 0;
    half4 snowRoad = 0;
    half3 colour = albedo;
    half surfaceSmoothness = smoothness;
    half3 normalTS = half3(0, 0, 1);
    half ao = 1;
    float slope = 0;

    [branch] if (r.kind == ROAD_DIRT || r.kind == ROAD_PATH)
    {
        // колеи; узкая дорога и тропинка — одна тропа
        float rutOffset = r.kind == ROAD_DIRT && h >= 1.2 ? min(0.8, h - 0.45) : 0;
        float rutHalf = rutOffset > 0 ? 0.25 : clamp(h * 0.55, 0.25, 0.45);
        float wander = (PuddleNoise(float2(r.along / 7.3, 1.7)) - 0.5) * 0.14;
        float d = abs(ax - rutOffset + wander + ragged * 0.12);
        half rut = RoadStripe(d, rutHalf, footprint, saturate(rutHalf * 2 / max(h, 0.5)));
        half carriageway = max(1 - smoothstep(h - 0.15, h + 0.15, ax + ragged), r.junction);
        // изношенная — голая почти вся; мало езженная — трава между колеями и у краёв
        half worn = carriageway * lerp(0.1, 0.8, r.wear) * (0.55 + 0.45 * PuddleNoise(xz / 1.3 + 9.1));
        half median = rutOffset > 0 ? 1 - smoothstep(rutOffset - rutHalf - 0.1, rutOffset - rutHalf + 0.1, ax) : 0;
        worn *= 1 - median * (1 - r.wear) * 0.7;
        // на перекрёстке колёса поворачивают — голая укатанная площадка, колеи на ней расплываются
        worn = max(worn, r.junction * (0.7 + 0.3 * PuddleNoise(xz / 2.1 + 4.7)));
        rut *= 1 - r.junction * 0.4;
        cover = max(rut, worn);
        snowRoad.xy = half2(carriageway, rut);

        RoadTexel dry = RoadTexture(_RoadLayers2.x, xz, dpdx, dpdy);
        RoadTexel mud = RoadTexture(_RoadLayers.z, xz, dpdx, dpdy);
        RoadTexel soil = RoadMix(dry, mud, wet);
        // сухая — пыльный светло-серо-бежевый укатанный грунт, колея светлее всего (на ней ни травинки); в сырость —
        // тёмная грязь, колея темнее. Где на снимке голая земля — чуть к её цвету
        half3 dust = RoadTone(dry, _RoadLayers2.x, half3(0.21, 0.185, 0.145)) * lerp(0.88, 1.08, rut);
        half3 dirt = RoadTone(mud, _RoadLayers.z, half3(0.06, 0.047, 0.034)) * lerp(1, 0.8, rut);
        half3 tint = lerp(1, clamp(albedo / max(dust, 0.01), 0.7, 1.3), bareSatellite * 0.3);
        colour = lerp(dust * tint, dirt, wet);
        surfaceSmoothness = lerp(soil.smoothness * 0.5, soil.smoothness, wet) + rut * 0.08;
        normalTS = soil.normalTS;
        ao = soil.ao;
        // колея вдавлена; вода стоит в колеях, местами (по шуму вдоль дороги), у изношенной — и между ними
        slope = RutSlope(x - wander * sign(x), rutOffset, rutHalf, 0.06) * saturate(1 - footprint / 0.4);
        half rutCore = 1 - smoothstep(rutHalf * 0.3, rutHalf * 0.9, d);
        // вода стоит лужами в низинах колеи (по шуму вдоль дороги), между ними — грязь; сплошная вода по всей колее была
        // ровной лентой
        hollow = max(rutCore, worn * r.wear * 0.6) * smoothstep(0.3, 0.8, PuddleNoise(float2(r.along / 5.3, x * 0.7 + 5.3)));
    }
    else if (r.kind == ROAD_GRAVEL)
    {
        cover = max(1 - smoothstep(h - 0.2, h + 0.2, ax + ragged * 1.2), r.junction);
        RoadTexel gravel = RoadTexture(_RoadLayers.y, xz, dpdx, dpdy);
        RoadTexel dry = RoadTexture(_RoadLayers2.x, xz, dpdx, dpdy);
        // колеи укатаны: щебня меньше, темнее и глаже
        float rutOffset = h >= 1.4 ? min(0.8, h - 0.5) : 0;
        float wander = (PuddleNoise(float2(r.along / 9.1, 4.3)) - 0.5) * 0.12;
        float d = abs(ax - rutOffset + wander);
        half rut = rutOffset > 0 ? RoadStripe(d, 0.28, footprint, saturate(0.56 / max(h, 0.5))) : 0;
        RoadTexel surface = RoadMix(gravel, dry, rut * 0.45);
        // серо-бежевый щебень, по колеям укатанный грунт с пылью
        half3 stone = lerp(RoadTone(gravel, _RoadLayers.y, half3(0.2, 0.185, 0.16)), RoadTone(dry, _RoadLayers2.x, half3(0.19, 0.17, 0.135)), rut * 0.5);
        half3 tint = lerp(1, clamp(albedo / max(stone, 0.01), 0.75, 1.3), bareSatellite * 0.25);
        colour = stone * tint * lerp(1, 0.7, wet);
        surfaceSmoothness = surface.smoothness * 0.6 + rut * 0.1;
        normalTS = surface.normalTS;
        ao = surface.ao;
        slope = rutOffset > 0 ? RutSlope(x, rutOffset, 0.28, 0.03) * saturate(1 - footprint / 0.4) : 0;
        hollow = rutOffset > 0 ? (1 - smoothstep(0.08, 0.25, d)) * smoothstep(0.35, 0.85, PuddleNoise(float2(r.along / 6.1, x * 0.7 + 2.1))) * 0.8 : 0;
        // зимой у узкой гравийки одна колея посередине
        snowRoad.xy = half2(cover, rutOffset > 0 ? rut : RoadStripe(ax, min(0.35, h * 0.5), footprint, saturate(0.7 / max(h * 2, 0.5))));
    }
    else if (r.kind == ROAD_ASPHALT)
    {
        // кромка: у бордюра ровная, в селе рваная по износу; на перекрёстке полотно сплошное (на стыке двух дорог карта
        // смешивает их расстояния до осей, и по ним кромка выходила лесенкой)
        float edgeNoise = r.curb ? 0 : ragged * lerp(0.25, 1.0, r.wear);
        half carriageway = max(1 - smoothstep(h - (r.curb ? 0.02 : 0.08), h + (r.curb ? 0.02 : 0.08), ax + edgeNoise), r.junction);
        half open = 1 - r.junction;
        cover = carriageway;
        pavedShare = carriageway;
        // зимой: полотно расчищено, по колёсам (~h/2 от оси) до асфальта, между ними и у кромки — серая каша; за кромкой
        // вал снега от плуга ~1.5 м (рваный; у бордюра — на тротуаре)
        half wheels = RoadStripe(abs(ax - h * 0.45), 0.45, footprint, saturate(0.9 / max(h, 0.5)));
        half berm = smoothstep(h - 0.05, h + 0.3, ax) * (1 - smoothstep(h + 0.9, h + 1.8, ax + ragged * 1.5)) * (1 - r.junction);
        snowRoad = half4(carriageway, wheels * carriageway, carriageway, berm);

        [branch] if (_RoadAsphalt.z > 0.5)
        {
            // полоса GeoWorld: u — от кромки до кромки, v — вдоль; каждые 12 м то прямо, то зеркально (шов совпадает)
            float stripLength = max(_RoadAsphalt.x, 1);
            float stripBlock = floor(r.along / stripLength);
            float mirror = RoadHash(float2(stripBlock, 5), 82u) < 0.5 ? -1 : 1;
            float vLocal = frac(r.along / stripLength);
            float2 uv = float2(saturate(x / (2 * h) + 0.5), mirror > 0 ? vLocal : 1 - vLocal);
            float2 uvdx = float2(dot(r.acrossDirection, dpdx) / (2 * h), dot(r.alongDirection, dpdx) / stripLength * mirror);
            float2 uvdy = float2(dot(r.acrossDirection, dpdy) / (2 * h), dot(r.alongDirection, dpdy) / stripLength * mirror);

            // вариант по износу; на стыке участков (36 м) — плавно в соседний
            float blockLength = stripLength * 3;
            float block = floor(r.along / blockLength);
            float within = r.along - block * blockLength;
            float variant = AsphaltVariant(block, r.wear);
            float neighbour = AsphaltVariant(within < blockLength * 0.5 ? block - 1 : block + 1, r.wear);
            half towards = (1 - smoothstep(0, 1.5, min(within, blockLength - within))) * 0.5;
            RoadTexel asphalt = AsphaltStrip(uv, variant, uvdx, uvdy, r, mirror);

            [branch] if (towards > 0.01 && neighbour != variant)
                asphalt = RoadMix(asphalt, AsphaltStrip(uv, neighbour, uvdx, uvdy, r, mirror), towards);

            // тон плывёт участками ремонта: крупный шум мира и свой сдвиг у каждого участка
            half tone = 1 + (PuddleNoise(xz / 45 + 13.7) - 0.5) * 0.18 + (RoadHash(float2(block, 9), 84u) - 0.5) * 0.08;
            colour = asphalt.colour * tone;
            surfaceSmoothness = asphalt.smoothness;
            normalTS = asphalt.normalTS;

            // на перекрёстке полосы двух дорог стыковались швом — там асфальт мира, в тоне полос
            [branch] if (r.junction > 0.01)
            {
                RoadTexel world = RoadTexture(_RoadLayers.x, xz, dpdx, dpdy);
                half3 worldColour = RoadTone(world, _RoadLayers.x, half3(0.04, 0.0395, 0.037) * lerp(1.0, 1.35, r.wear)) * tone;
                colour = lerp(colour, worldColour, r.junction);
                normalTS = normalize(lerp(normalTS, world.normalTS, r.junction));
            }
        }
        else
        {
            RoadTexel asphalt = RoadTexture(_RoadLayers.x, xz, dpdx, dpdy);
            colour = RoadTone(asphalt, _RoadLayers.x, lerp(0.075, 0.13, r.wear) * half3(1, 1, 0.95));
            surfaceSmoothness = asphalt.smoothness * 0.5;
            normalTS = asphalt.normalTS;
        }

        // разметка: ось штрихами; края сплошные, где нет бордюра; стёртая пятнами по износу
        [branch] if (r.marked)
        {
            half dash = step(frac(r.along / 12), 0.25);
            half axis = RoadStripe(ax, 0.06, footprint, 0.12 / max(h, 1)) * lerp(dash, 0.25, saturate(footprint / 0.5));
            half edges = r.curb ? 0 : RoadStripe(abs(ax - (h - 0.3)), 0.07, footprint, 0.14 / max(h, 1));
            half paint = max(axis, edges) * lerp(0.9, 0.35 + 0.55 * PuddleNoise(xz / 0.7 + 21.3), r.wear) * open;
            colour = lerp(colour, half3(0.55, 0.55, 0.52), paint);
            surfaceSmoothness = lerp(surfaceSmoothness, 0.3, paint);
        }

        [branch] if (r.curb)
        {
            // бордюр: светлый бетон шириной ~18 см за кромкой, щель у полотна темнее, верх чуть скруглён
            half curbBand = smoothstep(h - 0.005, h + 0.015, ax) * (1 - smoothstep(h + 0.16, h + 0.19, ax));
            half gap = 1 - smoothstep(0.0, 0.035, abs(ax - h - 0.005));
            RoadTexel concrete = RoadTexture(_RoadLayers.w, xz, dpdx, dpdy);
            half3 curbColour = RoadTone(concrete, _RoadLayers.w, half3(0.3, 0.29, 0.27)) * (0.85 + 0.3 * PuddleNoise(float2(r.along / 1.0, x)));
            curbBand = lerp(curbBand, 0.19 / max(h, 1), saturate(footprint / 0.3 - 0.5)) * open;
            colour = lerp(colour, curbColour, curbBand * (1 - carriageway));
            colour *= 1 - gap * 0.55 * saturate(1 - footprint / 0.15) * open;
            float bevel = (smoothstep(h + 0.12, h + 0.19, ax) - (1 - smoothstep(h + 0.005, h + 0.05, ax))) * 0.35 * sign(x);
            slope += bevel * saturate(1 - footprint / 0.2) * open;
            cover = max(cover, curbBand);
        }
        else
        {
            // обочина: гравий и земля полосой до ~0.7 м за кромкой
            half shoulder = (1 - smoothstep(h + 0.15, h + 0.7, ax + ragged * 0.6)) * (1 - carriageway) * 0.7 * open;
            RoadTexel gravel = RoadTexture(_RoadLayers.y, xz, dpdx, dpdy);
            half onShoulder = shoulder / max(max(cover, shoulder), 1e-3);
            colour = lerp(colour, RoadTone(gravel, _RoadLayers.y, half3(0.16, 0.145, 0.12)), onShoulder);
            normalTS = normalize(lerp(normalTS, gravel.normalTS, onShoulder));
            surfaceSmoothness = lerp(surfaceSmoothness, gravel.smoothness * 0.5, onShoulder);
            cover = max(cover, shoulder);
        }

        // вода: лужами вдоль кромки (полотно к краю ниже, у бордюра — лоток) и в выбоинах по колее у изношенных; где
        // лужи — по плавному шуму вдоль дороги (по клеткам края луж выходили прямыми)
        half gutter = smoothstep(h - (r.curb ? 0.6 : 1.0), h - 0.1, ax) * carriageway * open *
                      smoothstep(0.35, 0.85, PuddleNoise(float2(r.along / 4.3, x >= 0 ? 7.1 : 19.3)));
        half potholes = smoothstep(0.85 - r.wear * 0.3, 1.0 - r.wear * 0.3, PuddleNoise(float2(r.along / 6.3, x >= 0 ? 11.3 : 27.1)));
        half pothole = potholes * smoothstep(0.55, 0.75, PuddleNoise(xz / 0.9 + 33.7)) * (1 - smoothstep(0.35, 0.7, abs(ax - h * 0.45)));
        hollow = max(gutter * 0.85, pothole);
    }
    else if (r.kind == ROAD_PAVING)
    {
        cover = max(1 - smoothstep(h - 0.05, h + 0.05, ax + ragged * 0.15), r.junction);
        RoadTexel paving = RoadTexture(_RoadLayers.w, xz, dpdx, dpdy);
        colour = RoadTone(paving, _RoadLayers.w, half3(0.22, 0.21, 0.19));
        surfaceSmoothness = paving.smoothness * 0.6;
        normalTS = paving.normalTS;
        ao = paving.ao;
        snowRoad = half4(cover, 0, cover * 0.8, 0);
    }
    else
    {
        // рельсы: щебень насыпи, шпалы через 0.55 м, рельсы колеи 1520 мм; на переезде — бетонный настил
        cover = max(1 - smoothstep(h - 0.2, h + 0.2, ax + ragged * 0.5), r.junction);
        snowRoad.x = cover;
        RoadTexel ballast = RoadTexture(_RoadLayers.y, xz, dpdx, dpdy);
        colour = RoadTone(ballast, _RoadLayers.y, half3(0.16, 0.16, 0.165));
        surfaceSmoothness = ballast.smoothness * 0.5;
        normalTS = ballast.normalTS;
        ao = ballast.ao;
        half sleeper = (1 - smoothstep(1.25, 1.3, ax)) * RoadStripe(abs(frac(r.along / 0.55) - 0.5) * 0.55, 0.12, footprint, 0.44);
        colour = lerp(colour, half3(0.07, 0.06, 0.05), sleeper);

        [branch] if (r.junction > 0.01)
        {
            RoadTexel deck = RoadTexture(_RoadLayers.w, xz, dpdx, dpdy);
            colour = lerp(colour, RoadTone(deck, _RoadLayers.w, half3(0.13, 0.125, 0.115)), r.junction);
            normalTS = normalize(lerp(normalTS, deck.normalTS, r.junction));
        }

        half rail = RoadStripe(abs(ax - 0.76), 0.035, footprint, 0.1);
        colour = lerp(colour, half3(0.22, 0.22, 0.23), rail);
        surfaceSmoothness = lerp(surfaceSmoothness, 0.65, rail);
    }

    cover *= r.window * r.fade;
    winter = snowRoad * (r.window * r.fade);
    road = cover;
    paved = r.kind == ROAD_PAVING ? cover : pavedShare * r.window * r.fade;
    hollow *= cover;
    albedo = lerp(albedo, colour, cover);
    smoothness = lerp(smoothness, surfaceSmoothness, cover);
    occlusion = lerp(occlusion, occlusion * ao, cover);

    // рисунок — в плоскости XZ мира (как у GroundDetail), колея и бордюр — наклоном поперёк дороги
    normalTS.xy *= cover;
    float3 normalWS = inputData.normalWS;
    float3 tangentWS = normalize(cross(normalWS, float3(0, 0, 1)));
    float3 bitangentWS = cross(tangentWS, normalWS);
    normalWS = normalize(tangentWS * normalTS.x + bitangentWS * normalTS.y + normalWS * normalTS.z);
    float3 acrossWS = float3(r.acrossDirection.x, 0, r.acrossDirection.y);
    inputData.normalWS = normalize(normalWS - acrossWS * (slope * cover));
}

#endif
