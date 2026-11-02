// Лесная подстилка на террейне (Game/Terrain Lit (Wet), из GroundWetness.hlsl после поры года, до дорог и снега). Снимок
// тайла над лесом — кроны, и земля под деревьями была их цвета (зелёной, осенью оливковой). Карта ForestFloor вокруг
// камеры (_ForestFloorMap, по деревьям тайлов: R — подстилка, G — доля хвои, B — сомкнутость крон, A — доля листвы от
// голых осенью деревьев: осенью под ними свежего пёстрого листа больше и он дальше от ствола) решает, где под
// деревьями своя почва, а пора года (ForestFloor.SeasonLook) — какая: четыре слоя набора фактуры земли — основной, пятна,
// мох (в просветах крон), хвоя (по доле хвойных) — со своим цветом (тон: средний цвет, на который перекрашен рисунок,
// или множитель цвета текстуры). Из них берутся два самых весомых и смешиваются по высоте, как в GroundDetail. Вблизи
// (до _GroundDetailFade) — текстуры, дальше — их средний цвет. Под сомкнутыми кронами меньше неба (AO), а за дальностью
// теней, где тени крон уже не ложатся, — и солнца: подстилка там темнеет сама, иначе с высоты лес светился рыжим.
// Своих сэмплеров нет: карта — LOAD, массивы — сэмплером снега. Нужны до этого файла: Puddles.hlsl (шум), GroundDetail.hlsl.
#ifndef GAME_FOREST_FLOOR_INCLUDED
#define GAME_FOREST_FLOOR_INCLUDED

TEXTURE2D(_ForestFloorMap);
// xy — начало окна в мире, z — 1/шаг карты, w — текселей по стороне (0 — карты нет)
float4 _ForestFloorRect;
// слои массивов фактуры земли: основной, пятна, мох, хвоя
float4 _ForestFloorLayers;
// по слою: rgb — тон, a — 1: рисунок на средний цвет rgb, 0: цвет текстуры × rgb
float4 _ForestFloorTone[4];
// x — доля пятен, y — доля мха, z/w — с какой подстилки (R карты) она начинается и где сплошная
float4 _ForestFloorMix;
// под голыми деревьями (A карты): x — на столько больше пятен, y — подстилка начинается раньше на столько (осенью)
float4 _ForestFloorFresh;

// Карта в точке (билинейно по четырём текселям); у края окна сходит на нет.
half4 ForestFloorMap(float2 xz)
{
    half4 result = 0;

    [branch] if (_ForestFloorRect.w > 0.5)
    {
        float size = _ForestFloorRect.w;
        float2 uv = (xz - _ForestFloorRect.xy) * _ForestFloorRect.z / size;
        float2 edge = min(uv, 1 - uv);
        half inside = saturate(min(edge.x, edge.y) * 12);

        if (inside > 0)
        {
            float2 cell = uv * size - 0.5;
            float2 corner = floor(cell);
            float2 f = cell - corner;
            int2 c0 = clamp((int2)corner, 0, (int)size - 2);
            half4 a = LOAD_TEXTURE2D(_ForestFloorMap, c0);
            half4 b = LOAD_TEXTURE2D(_ForestFloorMap, c0 + int2(1, 0));
            half4 c = LOAD_TEXTURE2D(_ForestFloorMap, c0 + int2(0, 1));
            half4 d = LOAD_TEXTURE2D(_ForestFloorMap, c0 + int2(1, 1));
            result = lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y) * inside;
        }
    }

    return result;
}

struct ForestFloorTexel
{
    half3 colour;
    half3 normalTS;
    half ao;
    half smoothness;
    half height;
};

// Слой подстилки slot (0 основной, 1 пятна, 2 мох, 3 хвоя) в своём тоне; detail — доля текстуры (вдали — средний цвет).
ForestFloorTexel ForestFloorLayer(uint slot, float2 xz, float2 dpdx, float2 dpdy, half detail)
{
    float layer = _ForestFloorLayers[slot];
    float4 tone = _ForestFloorTone[slot];
    half3 average = _GroundDetailAverage[(uint)layer].rgb;
    half3 mean = tone.a > 0.5 ? (half3)tone.rgb : average * (half3)tone.rgb;

    ForestFloorTexel t;
    t.colour = mean;
    t.normalTS = half3(0, 0, 1);
    t.ao = 1;
    t.smoothness = 0.25;
    t.height = 0.5;

    [branch] if (detail > 0.001)
    {
        float4 settings = _GroundDetailLayer[(uint)layer];
        float2 uv = xz * settings.x;
        float2 dx = dpdx * settings.x;
        float2 dy = dpdy * settings.x;
        half4 albedo = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailAlbedo, sampler_SnowAlbedo, uv, layer, dx, dy);
        half4 packed = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailNormal, sampler_SnowAlbedo, uv, layer, dx, dy);
        half4 mask = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundDetailMask, sampler_SnowAlbedo, uv, layer, dx, dy);
        half3 colour = tone.a > 0.5 ? albedo.rgb / max(average, 0.005) * (half3)tone.rgb : albedo.rgb * (half3)tone.rgb;
        // BC5 (x в R) и DXT5nm (x в A, R = 1)
        half2 xy = (half2(packed.r * packed.a, packed.g) * 2 - 1) * settings.z;
        t.colour = lerp(mean, colour, detail);
        t.normalTS = half3(xy * detail, sqrt(saturate(1 - dot(xy, xy) * detail * detail)));
        t.ao = lerp(1, lerp(1, mask.g, settings.w), detail);
        t.smoothness = lerp(0.25, mask.b, detail);
        t.height = lerp(0.5, mask.r, detail);
    }

    return t;
}

// Доля в 0..1, которую шум n (0..1, у PuddleNoise в среднем ~0.5) переходит у доли share площади, с мягким краем.
half ForestFloorShare(half n, half share)
{
    half threshold = 1 - share;
    return share > 0.001 ? smoothstep(threshold - 0.06, threshold + 0.06, n) : 0;
}

// litter — доля подстилки в пикселе (для сырости: мокрый лист темнеет, как голая земля).
void ForestFloor(inout InputData inputData, float2 dpdx, float2 dpdy, float distanceToCamera, inout half3 albedo, inout half smoothness,
    inout half occlusion, out half litter)
{
    litter = 0;
    float3 positionWS = inputData.positionWS;
    half4 map = ForestFloorMap(positionWS.xz);

    [branch] if (map.r < 0.01 && map.b < 0.01)
        return;

    float2 xz = positionWS.xz;
    // край подстилки рваный, листва то гуще, то реже
    half ragged = (PuddleNoise(xz / 6.3 + 13.1) - 0.5) * 0.3 + (PuddleNoise(xz / 1.7 + 5.9) - 0.5) * 0.12;
    half fresh = map.a;
    half earlier = fresh * _ForestFloorFresh.y;
    litter = smoothstep(_ForestFloorMix.z - earlier, _ForestFloorMix.w - earlier, map.r + ragged);

    [branch] if (litter > 0.001)
    {
        // доли слоёв: хвоя — по доле хвойных; мох — пятнами, гуще в просветах крон; пятна — по своему шуму
        half needles = smoothstep(0.25, 0.75, map.g + (PuddleNoise(xz / 3.7 + 9.3) - 0.5) * 0.3);
        half moss = ForestFloorShare(PuddleNoise(xz / 3.3 + 31.7) * 0.75 + PuddleNoise(xz / 0.9 + 17.1) * 0.25, _ForestFloorMix.y * (1.3 - map.b * 0.6));
        half patches = ForestFloorShare(PuddleNoise(xz / 2.6 + 47.9) * 0.7 + PuddleNoise(xz / 0.8 + 23.3) * 0.3,
            saturate(_ForestFloorMix.x + fresh * _ForestFloorFresh.x));
        float4 weights = float4((1 - patches) * (1 - needles), patches * (1 - needles), 0, needles) * (1 - moss);
        weights.z = moss;

        // два самых весомых слоя; вес третьего вычитается — смена пары без скачка
        uint slot1 = 0, slot2 = 1;
        float best1 = -1, best2 = -1, best3 = -1;

        [unroll] for (uint k = 0; k < 4; k++)
        {
            float w = weights[k];
            if (w > best1) { best3 = best2; best2 = best1; slot2 = slot1; best1 = w; slot1 = k; }
            else if (w > best2) { best3 = best2; best2 = w; slot2 = k; }
            else if (w > best3) { best3 = w; }
        }

        half w1 = best1 - max(best3, 0);
        half w2 = max(best2 - max(best3, 0), 0);
        half total = max(w1 + w2, 1e-4);
        w1 /= total;
        w2 /= total;

        // вблизи — текстуры (как фактура земли), дальше — средний цвет
        half detail = saturate((_GroundDetailFade.z - distanceToCamera) * _GroundDetailFade.y);
        ForestFloorTexel first = ForestFloorLayer(slot1, xz, dpdx, dpdy, detail);
        ForestFloorTexel second = first;
        half blend1 = 1;
        half blend2 = 0;

        [branch] if (w2 > 0.004)
        {
            second = ForestFloorLayer(slot2, xz, dpdx, dpdy, detail);
            half a = first.height + w1 * 1.5;
            half b = second.height + w2 * 1.5;
            half top = max(a, b) - 0.25;
            blend1 = max(a - top, 0) * saturate(w1 * 8);
            blend2 = max(b - top, 0) * saturate(w2 * 8);
            half sum = max(blend1 + blend2, 1e-4);
            blend1 /= sum;
            blend2 /= sum;
        }

        half3 colour = first.colour * blend1 + second.colour * blend2;
        albedo = lerp(albedo, colour, litter);
        smoothness = lerp(smoothness, (first.smoothness * blend1 + second.smoothness * blend2) * 0.7, litter);
        occlusion *= lerp(1, first.ao * blend1 + second.ao * blend2, litter);

        half3 normalTS = normalize(first.normalTS * blend1 + second.normalTS * blend2);
        normalTS.xy *= litter;
        float3 normalWS = inputData.normalWS;
        float3 tangentWS = normalize(cross(normalWS, float3(0, 0, 1)));
        float3 bitangentWS = cross(tangentWS, normalWS);
        inputData.normalWS = normalize(tangentWS * normalTS.x + bitangentWS * normalTS.y + normalWS * normalTS.z);
    }

    // под сомкнутыми кронами неба меньше; за дальностью теней (или без теней) кроны не затеняют землю — темним сами
    half shadowFade = 1;
#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
    shadowFade = GetMainLightShadowFade(positionWS);
#endif
    half closed = map.b;
    occlusion *= lerp(1, 0.55, closed);
    albedo *= lerp(1, 0.45, closed * shadowFade);
}

#endif
