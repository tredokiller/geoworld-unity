// Вода и поток винтов, вода и дождь: общее для воды GeoWorld (Game/Water/*, WaterCore.hlsl) и луж и болот в шейдере
// террейна (GroundWetness.hlsl).
//  - рябь под дроном — поле RotorWashWater (_WaterRippleTex: R — высота, B — пена, A — согнанная ряска) в окне вокруг дрона
//    (_WaterRippleRect: xy — начало окна в мире, z — 1/размер, w — есть ли окно);
//  - струя: точка под дроном на воде и радиус струи у воды (_WaterWashPosition, w — м: 0.4 + 0.8 высоты, как у листьев)
//    и воздух у края струи — доля полного потока 20 м/с (_WaterWashPower); дальше он слабеет ~(струя/r)^0.9. По нему —
//    рябь и тусклый круг (WaterWashAir/Rough/Smoothness), гладь под струёй (WaterWashCalm), выдутая лужа (WaterWashBlow);
//    где поток согнал ряску и плёнку — помнит само поле (A, WaterWashClear);
//  - капли дождя: кольца по всей воде у камеры, сила — _WaterRain (0..1, ставит GroundWetness по погоде).
#ifndef GAME_WATER_INTERACTION_INCLUDED
#define GAME_WATER_INTERACTION_INCLUDED

TEXTURE2D(_WaterRippleTex);
// 1/разрешение, разрешение поля (ставит RotorWashWater: у глобальной текстуры _TexelSize может не быть)
float4 _WaterRippleTexel;
float4 _WaterRippleRect;
float4 _WaterWashPosition;
float _WaterWashPower;
float _WaterRain;

// Наклон поверхности (dh/dx, dh/dz) от ряби под дроном и пена на ней. Вне окна — ноль.
float2 WaterRippleSlope(float3 positionWS, out half foam)
{
    foam = 0;
    // сколько метров воды накрывает пиксель — до ранних выходов (производные)
    float footprint = length(fwidth(positionWS.xz));

    if (_WaterRippleRect.w < 0.5)
        return 0;

    float2 uv = (positionWS.xz - _WaterRippleRect.xy) * _WaterRippleRect.z;

    if (any(uv < 0) || any(uv > 1))
        return 0;

    float2 texel = _WaterRippleTexel.xy;
    float left = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_LinearClamp, uv - float2(texel.x, 0), 0).r;
    float right = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_LinearClamp, uv + float2(texel.x, 0), 0).r;
    float down = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_LinearClamp, uv - float2(0, texel.y), 0).r;
    float up = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_LinearClamp, uv + float2(0, texel.y), 0).r;
    foam = SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_LinearClamp, uv, 0).b;

    // размер ячейки поля в метрах: окно 1/z на число текселей
    float cell = 1.0 / (_WaterRippleRect.z * _WaterRippleTexel.z);
    // поле гаснет у краёв окна (2 м) и кругом к 22 м от дрона (окно переезжает за ним каждые 3 м и краем ближе 21 м не
    // подходит — квадрат окна на воде не виден); где пиксель накрывает больше ~0.3 м воды, рябь только искрит — гасим
    float2 edge = min(uv, 1 - uv);
    float fade = saturate(min(edge.x, edge.y) * 24) *
                 saturate((22 - distance(positionWS.xz, _WaterWashPosition.xz)) / 6) *
                 (1 - smoothstep(0.15, 0.4, footprint));
    return float2(right - left, up - down) / (2 * cell) * fade;
}

// Насколько поток винтов сгладил здесь мелкую волну: 1 — гладкая вода под струёй, 0 — ветер как был.
half WaterWashCalm(float3 positionWS)
{
    if (_WaterWashPower <= 0.001)
        return 0;

    float r = distance(positionWS.xz, _WaterWashPosition.xz) / max(_WaterWashPosition.w, 0.1);
    return saturate(_WaterWashPower * 3) * exp(-r * r);
}

// Воздух от винтов у воды здесь, доля полного потока (×20 м/с) — как у листьев у земли (RotorWashLeaves): под струёй
// 0.6–1 от края, дальше слабеет ~(струя/r)^0.9; с 20 до 28 м от дрона сходит на нет.
half WaterWashAir(float3 positionWS)
{
    if (_WaterWashPower <= 0.001)
        return 0;

    float jet = max(_WaterWashPosition.w, 0.1);
    float r = distance(positionWS.xz, _WaterWashPosition.xz);
    float spread = r < jet ? lerp(0.6, 1, r / jet) : pow(jet / max(r, 1e-3), 0.9);
    return _WaterWashPower * spread * saturate((28 - r) / 8);
}

// Насколько поток рвёт гладь в тусклую рябь, в которой не видно отражений: с ~0.6 м/с воздуха, сплошь — с 3 м/с (на
// висении в 4 м — круг ~7 м). Дальше рябь только кольцами и полосами — отражение берега в ней дрожит, а не пропадает.
half WaterWashRough(half air)
{
    return smoothstep(0.03, 0.15, air);
}

// Зарябившая вода — не зеркало: небо в ней размыто, под косым взглядом она тусклее (тусклый круг вокруг дрона).
half WaterWashSmoothness(half smoothness, half rough)
{
    return lerp(smoothness, 0.55, rough * 0.85);
}

// Пена от винтов кружевом (0..1): поле пены (RotorWashWater, B) решает, сколько её, рисунок пены (pattern, 0..1) — где:
// слабое поле — редкие клочья на светлых местах рисунка, сильное — плотное кружево с прорехами. Раньше пена была полем
// с добавкой поверх рисунка и стояла сплошным белым покровом.
half WaterWashLace(half field, half pattern)
{
    return saturate((field * 1.4 - (1.0 - pattern)) * 2.0);
}

// Насколько поток согнал отсюда ряску, водоросли и плёнку (0..1) — поле RotorWashWater (A): с ~1.6 м/с воздуха, совсем —
// с ~4.4 м/с; окно раскрывается за полсекунды и затягивается за ~8 с там, где его выдуло.
half WaterWashClear(float3 positionWS)
{
    if (_WaterRippleRect.w < 0.5)
        return 0;

    float2 uv = (positionWS.xz - _WaterRippleRect.xy) * _WaterRippleRect.z;

    if (any(uv < 0) || any(uv > 1))
        return 0;

    return SAMPLE_TEXTURE2D_LOD(_WaterRippleTex, sampler_LinearClamp, uv, 0).a;
}

// Сколько воды поток выдул из тонкой лужи (0..0.75): под струёй давит весь поток, дальше — стенная струя ~(струя/r)^0.9;
// вода уходит, где напор быстрее ~5 м/с, почти вся — с ~10 м/с. На взлёте в полный газ — на ~2 м вокруг дрона, на висении
// в 1–2 м над лужей — на 1.5–3 м, с 4 м уже нет.
half WaterWashBlow(float3 positionWS)
{
    if (_WaterWashPower <= 0.25)
        return 0;

    float jet = max(_WaterWashPosition.w, 0.1);
    float r = distance(positionWS.xz, _WaterWashPosition.xz);
    float push = _WaterWashPower * min(1, pow(jet / max(r, 1e-3), 0.9));
    return 0.75 * smoothstep(0.25, 0.5, push);
}

// Случайное 0..1 для целой клетки и номера выборки — целочисленным хешем (Jenkins из Core RP): float-хеш вида
// frac(p · 233.34) на мировых координатах в километрах терял точность, и кольца дождя шли синхронными рядами.
float WaterHash(float2 cell, uint salt)
{
    int2 id = (int2)floor(cell);
    return GenerateHashedRandomFloat(uint3(asuint(id.x), asuint(id.y), salt));
}

// Кольца от капель: два слоя ячеек разного размера, в каждой ячейке своя капля со своим временем. Наклон
// (dh/dx, dh/dz) — производная бегущего кольца с затуханием. Дальше ~40 м кольца не видны — не считаем.
float2 WaterRainSlope(float3 positionWS, float distanceToCamera, float rain)
{
    if (rain <= 0.01 || distanceToCamera > 45)
        return 0;

    float2 slope = 0;
    float fade = saturate(1 - distanceToCamera / 45);

    [unroll]
    for (int layer = 0; layer < 2; layer++)
    {
        float cellsPerMetre = layer == 0 ? 1.7 : 2.9;
        float speed = layer == 0 ? 0.9 : 1.25;
        // кольцо ~0.1 м: когда пиксель накрывает больше полукольца, рябь только искрит — гасим по размеру пикселя
        float footprint = max(length(fwidth(positionWS.xz)) * cellsPerMetre, 1e-4);
        float visible = saturate(0.15 / footprint - 1);

        if (visible <= 0)
            continue;

        float2 q = positionWS.xz * cellsPerMetre + layer * 37.17;
        float2 cell = floor(q);
        float2 local = q - cell;

        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            [unroll]
            for (int x = -1; x <= 1; x++)
            {
                int2 id = (int2)(cell + float2(x, y));
                // один хеш на клетку, четыре байта: есть ли капля, её время и место
                uint h = JenkinsHash(uint3(asuint(id.x), asuint(id.y), (uint)layer));
                float4 random = float4(h & 255u, (h >> 8) & 255u, (h >> 16) & 255u, h >> 24) / 255.0;

                // в слабый дождь капает не в каждой ячейке
                if (random.x > rain)
                    continue;

                float phase = frac(_Time.y * speed + random.y);
                float2 centre = float2(x, y) + random.zw;
                float2 offset = local - centre;
                float r = length(offset);
                // кольцо расходится до ~0.8 ячейки и гаснет
                float front = (r - phase * 0.8) * 14;
                float wave = -sin(front * 2.4) * exp(-front * front * 0.35) * (1 - phase) * (1 - phase) * visible;
                slope += offset / max(r, 1e-3) * wave;
            }
        }
    }

    return slope * 0.35 * fade;
}

#endif
