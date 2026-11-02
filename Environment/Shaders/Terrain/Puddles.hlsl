// Где стоит вода на земле — общее для террейна (GroundWetness.hlsl рисует воду) и травы (PuddleSink.hlsl: растения в
// воде уходят под неё). Решение одно в обоих шейдерах, поэтому трава оседает ровно там, где террейн рисует воду.
//  - лужи в дождь: _PuddleLevel (0..1) — насколько набрались (GroundWetness), где им быть — R карты _PuddleLand
//    (пригодность земли по карте земель GeoWorld с уклоном рельефа);
//  - болото: G карты — стоячая вода между кочек круглый год, в дождь её больше;
//  - форма — шум трёх масштабов в мире: вода растёт из одних и тех же мест и сливается.
// Карта — 8 м на тексель в окне 2 км вокруг камеры (xy _PuddleLandRect — начало окна в мире, z — 1/размер, w — есть ли),
// под ней дальняя — та же, 32 м на тексель в окне 8 км (_PuddleFarLand, _PuddleFarRect): на все загруженные тайлы.
#ifndef GAME_PUDDLES_INCLUDED
#define GAME_PUDDLES_INCLUDED

float _PuddleLevel;
TEXTURE2D(_PuddleLand);
float4 _PuddleLandRect;
TEXTURE2D(_PuddleFarLand);
float4 _PuddleFarRect;

// Пригодность под лужу (R), болото (G) и пашня (B) по карте: у камеры — ближняя, за ней — дальняя; последние 128 м до
// края ближнего окна одна переходит в другую. Без дальней лужи и болота с высоты обрывались прямой линией по краю окна.
// Вне обеих — как луг, без болота и пашни.
half3 GroundLand(float3 positionWS)
{
    half3 land = half3(0.25, 0, 0);

    if (_PuddleFarRect.w > 0.5)
    {
        float2 farUV = (positionWS.xz - _PuddleFarRect.xy) * _PuddleFarRect.z;

        if (all(farUV >= 0) && all(farUV <= 1))
        {
            land = SAMPLE_TEXTURE2D_LOD(_PuddleFarLand, sampler_LinearClamp, farUV, 0).rgb;
            // пашня гаснет к краю дальнего окна (последние 500 м): за ним убранные поля обзорной карты не обрываются
            // прямой линией
            float2 farEdge = min(farUV, 1 - farUV);
            land.b *= saturate(min(farEdge.x, farEdge.y) * 16);
        }
    }

    if (_PuddleLandRect.w > 0.5)
    {
        float2 uv = (positionWS.xz - _PuddleLandRect.xy) * _PuddleLandRect.z;
        float2 edge = min(uv, 1 - uv);
        // до края окна в его долях: 1/16 окна в 2 км — 128 м
        half near = saturate(min(edge.x, edge.y) * 16);

        if (near > 0)
            land = lerp(land, SAMPLE_TEXTURE2D_LOD(_PuddleLand, sampler_LinearClamp, uv, 0).rgb, near);
    }

    return land;
}

half2 PuddleLand(float3 positionWS)
{
    return GroundLand(positionWS).rg;
}

// Случайное 0..1 для целой клетки — целочисленным хешем (Jenkins из Random.hlsl Core RP): на мировых координатах в
// километрах float-хеш терял точность.
float PuddleHash(float2 cell)
{
    int2 id = (int2)floor(cell);
    return GenerateHashedRandomFloat(uint3(asuint(id.x), asuint(id.y), 16u));
}

float PuddleNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3 - 2 * f);
    float a = PuddleHash(i), b = PuddleHash(i + float2(1, 0));
    float c = PuddleHash(i + float2(0, 1)), d = PuddleHash(i + 1);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// Доля пашни (B карты) в точке, сдвинутой шумом на ±8 м: клетки карты земель (10 м) по краю поля не встают лесенкой.
half GroundFieldShare(float3 positionWS)
{
    float2 p = positionWS.xz / 19.0;
    float2 warp = float2(PuddleNoise(p), PuddleNoise(p + 31.7)) * 16 - 8;
    return GroundLand(positionWS + float3(warp.x, 0, warp.y)).b;
}

// Крупные слои «рельефа» воды: шум ~7 и ~2.5 м и среднее мелкого слоя.
float PuddleFieldCoarse(float2 p)
{
    return PuddleNoise(p / 7.0) * 0.55 + PuddleNoise(p / 2.5 + 17.1) * 0.3 + 0.075;
}

// «Рельеф» воды (≈ 0.51 ± 0.14): шум ~7, ~2.5 и ~0.9 м. Дальше 80–160 м мелкий слой — средним значением: край там не
// мельтешит (и не считаем его).
float PuddleField(float2 p, float distanceToCamera)
{
    float field = PuddleFieldCoarse(p);

    if (distanceToCamera < 160)
        field += (PuddleNoise(p / 0.9 + 31.7) * 0.15 - 0.075) * saturate((160 - distanceToCamera) / 80);

    return field;
}

// Порог шума для луж по пригодности: в полную воду грунтовка под лужами на ~35–40 %, голая земля ~18 %, пашня ~10 %,
// луг ~3 %; пока лужи набираются, порог выше — сперва вода только в самых низких местах.
float PuddleThreshold(half suitability)
{
    return 0.873 - 0.313 * suitability + (1 - _PuddleLevel) * 0.3;
}

// Порог для болота: в сухую погоду открытая вода между кочек и тростника на ~8 % болота, в долгий дождь — на ~25 %
// (болото по карте земель — это и сырые луга поймы, сплошной воды там нет).
float MarshThreshold(half marsh)
{
    return lerp(1.2, lerp(0.70, 0.60, _PuddleLevel), marsh);
}

#endif
