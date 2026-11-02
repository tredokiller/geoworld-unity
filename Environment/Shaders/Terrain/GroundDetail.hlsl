// Фактура земли вблизи камеры поверх спутника (Game/Terrain Lit (Wet)): снимок тайла ~0.5 м на тексель, с высоты в
// несколько метров он — размытые пятна. До _GroundDetailFade.z метров (плавно гаснет с .x) на него кладутся тайлящиеся
// фото-текстуры (GroundDetail: массивы альбедо, нормалей и масок — R высота, G AO, B гладкость), но цвет остаётся
// спутниковым: текстура даёт только свой рисунок — отношение к своему среднему цвету — и рельеф. Издали поэтому картинка
// та же (мипы сходятся к среднему, отношение — к 1), шва и момента подмены нет.
// Какую текстуру где:
//  - класс земли GeoWorld (~10 м, окно _GroundDetailClasses вокруг камеры; четыре соседние клетки, смешанные по
//    расстоянию до них, в точке, сдвинутой шумом на ±4 м, — границы классов не идут квадратами) → три слоя класса
//    (_GroundDetailClass): зелёный, голый тёмный, голый светлый;
//  - между ними выбирает цвет спутника в пикселе: зелень — трава, бурое — грунт (тёмное — пашня, грязь; светлое — стерня,
//    сухое поле, песок). Граница поэтому идёт по снимку, а не по клеткам карты;
//  - из всех претендентов берутся два самых весомых (вес третьего вычитается — смена пары непрерывна) и смешиваются по
//    высоте: на стыке трава прорастает из щелей грунта, а не просвечивает.
// Только слои, растянутые на весь террейн (спутник тайла; на точке взлёта — его доля под её собственными слоями).
// Своих сэмплеров нет: в проходе ForwardLit их уже под предел 16, а лишний молча ломал другую привязку — земля
// становилась белым зеркалом. Массивы читаются сэмплером снега (повтор, трилинейный, анизотропия 8), карта классов —
// без сэмплера (LOAD). Нужны Puddles.hlsl (шум) и Snow.hlsl (сэмплер) до этого файла.
#ifndef GAME_GROUND_DETAIL_INCLUDED
#define GAME_GROUND_DETAIL_INCLUDED

#define GROUND_DETAIL_MAX_LAYERS 32
#define GROUND_DETAIL_CLASSES 20

TEXTURE2D_ARRAY(_GroundDetailAlbedo);
TEXTURE2D_ARRAY(_GroundDetailNormal);
TEXTURE2D_ARRAY(_GroundDetailMask);
TEXTURE2D(_GroundDetailClasses);
// xy — начало окна классов в мире, z — 1/размер клетки, w — клеток по стороне (0 — фактуры нет)
float4 _GroundDetailRect;
// x — с какой дистанции гаснет, y — 1/(конец − начало), z — конец
float4 _GroundDetailFade;
// по слою: x — 1/размер повтора (м), y — контраст рисунка, z — сила нормалей, w — сила AO
float4 _GroundDetailLayer[GROUND_DETAIL_MAX_LAYERS];
// по слою: средний цвет текстуры (линейный)
float4 _GroundDetailAverage[GROUND_DETAIL_MAX_LAYERS];
// по классу земли: слои зелёный, голый тёмный, голый светлый
float4 _GroundDetailClass[GROUND_DETAIL_CLASSES];

struct GroundDetailSample
{
    half3 ratio;
    half3 normalTS;
    half ao;
    half smoothness;
    half height;
};

GroundDetailSample SampleGroundDetailLayer(float layer, float2 positionXZ, float2 dpdx, float2 dpdy)
{
    float4 settings = _GroundDetailLayer[(uint)layer];
    float2 uv = positionXZ * settings.x;
    float2 dx = dpdx * settings.x;
    float2 dy = dpdy * settings.x;
    half4 albedo = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailAlbedo, sampler_SnowAlbedo, uv, layer, dx, dy);
    half4 packed = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailNormal, sampler_SnowAlbedo, uv, layer, dx, dy);
    half4 mask = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailMask, sampler_SnowAlbedo, uv, layer, dx, dy);

    GroundDetailSample s;
    // рисунок — в основном яркостью, оттенок текстуры — на треть: синего в земле мало, и его отношение к среднему
    // красило траву пятнами в лиловый
    half3 average = _GroundDetailAverage[(uint)layer].rgb;
    half3 luma = half3(0.2126, 0.7152, 0.0722);
    half brightness = dot(albedo.rgb, luma) / max(dot(average, luma), 0.005);
    s.ratio = lerp(1, lerp(brightness.xxx, albedo.rgb / max(average, 0.005), 0.35), settings.y);
    // BC5 (x в R) и DXT5nm (x в A, R = 1)
    half2 xy = (half2(packed.r * packed.a, packed.g) * 2 - 1) * settings.z;
    s.normalTS = half3(xy, sqrt(saturate(1 - dot(xy, xy))));
    s.ao = lerp(1, mask.g, settings.w);
    s.smoothness = mask.b;
    s.height = mask.r;
    return s;
}

// Претендент на слой: вес в первую запись с тем же слоем.
#define GROUND_DETAIL_MERGE(i) [unroll] for (int j##i = 0; j##i < i; j##i++) if (ids[j##i] == ids[i]) { weights[j##i] += weights[i]; weights[i] = 0; }

void GroundDetail(inout InputData inputData, half satellite, inout half3 albedo, inout half smoothness, inout half occlusion)
{
    float3 positionWS = inputData.positionWS;
    // производные — до ветвлений: по ним мипы текстур
    float2 dpdx = ddx(positionWS.xz);
    float2 dpdy = ddy(positionWS.xz);
    float distanceToCamera = distance(positionWS, _WorldSpaceCameraPos);
    half fade = saturate((_GroundDetailFade.z - distanceToCamera) * _GroundDetailFade.y) * satellite;

    [branch] if (_GroundDetailRect.w < 0.5 || fade <= 0.001)
        return;

    // классы четырёх соседних клеток в точке, сдвинутой шумом: граница классов — волнистая, а не по клеткам
    float2 jitter = float2(PuddleNoise(positionWS.xz / 9.0 + 3.7), PuddleNoise(positionWS.xz / 9.0 + 41.3)) * 8 - 4;
    float2 cell = (positionWS.xz + jitter - _GroundDetailRect.xy) * _GroundDetailRect.z - 0.5;
    float2 corner = floor(cell);
    float2 f = cell - corner;
    int2 c0 = clamp((int2)corner, 0, (int)_GroundDetailRect.w - 2);
    float4 classes = round(float4(
        LOAD_TEXTURE2D(_GroundDetailClasses, c0 + int2(0, 1)).r, LOAD_TEXTURE2D(_GroundDetailClasses, c0 + int2(1, 1)).r,
        LOAD_TEXTURE2D(_GroundDetailClasses, c0 + int2(1, 0)).r, LOAD_TEXTURE2D(_GroundDetailClasses, c0).r) * 255);
    // веса клеток (0,1), (1,1), (1,0), (0,0)
    float4 bilinear = float4((1 - f.x) * f.y, f.x * f.y, f.x * (1 - f.y), (1 - f.x) * (1 - f.y));

    // спутник в пикселе: зелень — трава; бурое — грунт, тёмное (пашня, грязь) или светлое (стерня, сухое поле, песок)
    half greenness = (albedo.g - max(albedo.r, albedo.b)) / max(albedo.g, 0.02);
    half bare = 1 - smoothstep(-0.05, 0.2, greenness);
    half light = smoothstep(0.07, 0.13, dot(albedo, half3(0.2126, 0.7152, 0.0722)));
    float3 slots = float3(1 - bare, bare * (1 - light), bare * light);

    float ids[12];
    float weights[12];
    [unroll] for (int k = 0; k < 4; k++)
    {
        float4 layers = _GroundDetailClass[(uint)classes[k]];
        ids[k * 3] = layers.x;
        ids[k * 3 + 1] = layers.y;
        ids[k * 3 + 2] = layers.z;
        weights[k * 3] = bilinear[k] * slots.x;
        weights[k * 3 + 1] = bilinear[k] * slots.y;
        weights[k * 3 + 2] = bilinear[k] * slots.z;
    }

    GROUND_DETAIL_MERGE(1) GROUND_DETAIL_MERGE(2) GROUND_DETAIL_MERGE(3) GROUND_DETAIL_MERGE(4) GROUND_DETAIL_MERGE(5)
    GROUND_DETAIL_MERGE(6) GROUND_DETAIL_MERGE(7) GROUND_DETAIL_MERGE(8) GROUND_DETAIL_MERGE(9) GROUND_DETAIL_MERGE(10)
    GROUND_DETAIL_MERGE(11)

    // два самых весомых слоя; вес третьего вычитается — когда пара меняется, уходящий слой уже с нулевым весом
    float best1 = -1, best2 = -1, best3 = -1;
    float layer1 = 0, layer2 = 0;
    [unroll] for (int n = 0; n < 12; n++)
    {
        float w = weights[n];
        if (w > best1) { best3 = best2; best2 = best1; layer2 = layer1; best1 = w; layer1 = ids[n]; }
        else if (w > best2) { best3 = best2; best2 = w; layer2 = ids[n]; }
        else if (w > best3) { best3 = w; }
    }

    half w1 = best1 - max(best3, 0);
    half w2 = max(best2 - max(best3, 0), 0);
    half total = max(w1 + w2, 1e-4);
    w1 /= total;
    w2 /= total;

    GroundDetailSample first = SampleGroundDetailLayer(layer1, positionWS.xz, dpdx, dpdy);
    GroundDetailSample second = first;
    half blend1 = 1;
    half blend2 = 0;

    [branch] if (w2 > 0.004)
    {
        second = SampleGroundDetailLayer(layer2, positionWS.xz, dpdx, dpdy);
        // по высоте: на стыке верх одного рисунка закрывает низ другого; у слоя на выходе из пары вес к нулю — без скачка
        half a = first.height + w1 * 1.5;
        half b = second.height + w2 * 1.5;
        half top = max(a, b) - 0.25;
        blend1 = max(a - top, 0) * saturate(w1 * 8);
        blend2 = max(b - top, 0) * saturate(w2 * 8);
        half sum = max(blend1 + blend2, 1e-4);
        blend1 /= sum;
        blend2 /= sum;
    }

    half3 ratio = clamp(first.ratio * blend1 + second.ratio * blend2, 0, 3);
    albedo *= lerp(1, ratio, fade);
    smoothness = lerp(smoothness, (first.smoothness * blend1 + second.smoothness * blend2) * 0.7, fade);
    occlusion *= lerp(1, first.ao * blend1 + second.ao * blend2, fade);

    // рисунок — в плоскости XZ мира: касательная по +X, бинормаль по +Z вдоль склона
    half3 normalTS = normalize(first.normalTS * blend1 + second.normalTS * blend2);
    normalTS.xy *= fade;
    float3 normalWS = inputData.normalWS;
    float3 tangentWS = normalize(cross(normalWS, float3(0, 0, 1)));
    float3 bitangentWS = cross(tangentWS, normalWS);
    inputData.normalWS = normalize(tangentWS * normalTS.x + bitangentWS * normalTS.y + normalWS * normalTS.z);
}

#endif
