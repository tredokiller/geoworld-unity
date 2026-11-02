// Снег зимой — общий для террейна (GroundWetness.hlsl), льда стоячей воды (WaterCore.hlsl) и дальней карты
// (GeoOverviewSeasons.shader). Чтобы снег не был плоским белым пятном:
//  - текстура снега (_SnowAlbedo, _SnowNormal — Seasons кладёт глобально из Resources/Seasons) в двух масштабах: зерно и
//    мелкие заструги ~3 м и надувы ~12 м, повёрнутые на 37° к первым, — повтор не читается;
//  - крупные волны надувов поперёк ветра зимы и пятна светлее-темнее в десятки метров: под низким зимним солнцем снег
//    лепится светом;
//  - сквозь снег проступает земля: где под ним темнее (лес, кусты, пашня, межи), снег серее — с высоты видны поля,
//    лесополосы и дороги, как на зимних снимках;
//  - на дорогах снег накатанный, серый, с грязью;
//  - искры кристаллов у самой камеры — крошечные грани-зеркала, блеск им даёт свет URP (в тени их нет);
//  - неполный покров (_SnowCover < 1) — плавный градиентный шум с искажением (без квадратов шума значений): раньше тает
//    на южных склонах, тёмной земле и дорогах, край рвётся языками по текстуре снега.
// Всё зовётся внутри ветвлений по поре года, поэтому производные (для мипов и ширины края) берутся снаружи и передаются.
#ifndef GAME_SNOW_INCLUDED
#define GAME_SNOW_INCLUDED

#include "Seasons.hlsl"

TEXTURE2D(_SnowAlbedo);
SAMPLER(sampler_SnowAlbedo);
TEXTURE2D(_SnowNormal);
// 1 — текстуры снега заданы (Seasons), иначе снег ровного цвета
float _SnowTextures;

// Куда дует ветер зимы (мир xz): преобладающий северо-западный. Надувы лежат по нему и не крутятся вслед за ветром
// тренировки.
#define SNOW_WIND float2(0.8, -0.6)
// средний цвет текстуры снега (линейный): к нему приводим её к цвету снега SEASON_SNOW_COLOR
#define SNOW_TEXTURE_MEAN half3(0.89, 0.9, 0.94)

float SnowHash(float2 cell, uint salt)
{
    int2 id = (int2)floor(cell);
    return GenerateHashedRandomFloat(uint3(asuint(id.x), asuint(id.y), salt));
}

float2 SnowGradient(int2 cell, uint salt)
{
    uint h = JenkinsHash(uint3(asuint(cell.x), asuint(cell.y), salt));
    float s, c;
    sincos((h >> 8) * (6.2831853 / 16777216.0), s, c);
    return float2(c, s);
}

// Градиентный шум по миру (≈ −0.5..0.5): изолинии плавные, без квадратов и ромбов шума значений.
float SnowNoise(float2 p, uint salt)
{
    float2 i = floor(p);
    float2 f = p - i;
    float2 u = f * f * f * (f * (f * 6 - 15) + 10);
    int2 c = (int2)i;
    float a = dot(SnowGradient(c, salt), f);
    float b = dot(SnowGradient(c + int2(1, 0), salt), f - float2(1, 0));
    float d = dot(SnowGradient(c + int2(0, 1), salt), f - float2(0, 1));
    float e = dot(SnowGradient(c + int2(1, 1), salt), f - 1);
    return lerp(lerp(a, b, u.x), lerp(d, e, u.x), u.y);
}

// Насколько снег сереет от земли под ним (1 — чистый): тёмное (лес, кусты, пашня) проступает, светлое поле — нет.
half SnowOverGround(half3 ground)
{
    return lerp(0.8, 1.0, smoothstep(0.03, 0.22, SeasonLuminance(ground)));
}

struct SnowSurface
{
    half3 albedo;
    float3 normalWS;
    half smoothness;
    // «высота» в текстуре снега (≈ 0.5 ± 0.3): по ней рвётся край неполного покрова
    half height;
};

// Снег в точке. ground — цвет земли под ним (что проступает), packed — накатанный снег дороги (0..1); dpdx, dpdy —
// производные мировых xz по экрану (взяты вне ветвлений).
SnowSurface GetSnowSurface(float3 positionWS, half3 geometryNormalWS, half3 ground, half packed, float distanceToCamera,
    float2 dpdx, float2 dpdy)
{
    float2 p = positionWS.xz;
    half3 albedo = SEASON_SNOW_COLOR;
    float2 tilt = 0;
    half height = 0.5;

    if (_SnowTextures > 0.5)
    {
        // три масштаба одной текстуры, каждый повёрнут: ~3 м — у камеры (дальше её повтор читался сеткой), ~12 м —
        // и вдали слабее, ~41 м — пологие волны, повтор средних не виден
        float2 uvA = p / 3.2;
        float2 uvB = float2(p.x * 0.8 - p.y * 0.6, p.x * 0.6 + p.y * 0.8) / 11.7 + 0.37;
        float2 dxB = float2(dpdx.x * 0.8 - dpdx.y * 0.6, dpdx.x * 0.6 + dpdx.y * 0.8) / 11.7;
        float2 dyB = float2(dpdy.x * 0.8 - dpdy.y * 0.6, dpdy.x * 0.6 + dpdy.y * 0.8) / 11.7;
        float2 uvC = float2(p.x * 0.53 + p.y * 0.85, p.y * 0.53 - p.x * 0.85) / 41.0 + 0.71;
        float2 dxC = float2(dpdx.x * 0.53 + dpdx.y * 0.85, dpdx.y * 0.53 - dpdx.x * 0.85) / 41.0;
        float2 dyC = float2(dpdy.x * 0.53 + dpdy.y * 0.85, dpdy.y * 0.53 - dpdy.x * 0.85) / 41.0;
        half3 a = SAMPLE_TEXTURE2D_GRAD(_SnowAlbedo, sampler_SnowAlbedo, uvA, dpdx / 3.2, dpdy / 3.2).rgb;
        half3 b = SAMPLE_TEXTURE2D_GRAD(_SnowAlbedo, sampler_SnowAlbedo, uvB, dxB, dyB).rgb;
        half3 na = UnpackNormal(SAMPLE_TEXTURE2D_GRAD(_SnowNormal, sampler_SnowAlbedo, uvA, dpdx / 3.2, dpdy / 3.2));
        half3 nb = UnpackNormal(SAMPLE_TEXTURE2D_GRAD(_SnowNormal, sampler_SnowAlbedo, uvB, dxB, dyB));
        half3 nc = UnpackNormal(SAMPLE_TEXTURE2D_GRAD(_SnowNormal, sampler_SnowAlbedo, uvC, dxC, dyC));
        // повёрнутые слои: наклон — обратно в оси мира
        nb.xy = half2(nb.x * 0.8 + nb.y * 0.6, nb.y * 0.8 - nb.x * 0.6);
        nc.xy = half2(nc.x * 0.53 - nc.y * 0.85, nc.x * 0.85 + nc.y * 0.53);
        half nearA = 1 - smoothstep(10, 35, distanceToCamera);
        half midB = lerp(1, 0.35, smoothstep(25, 120, distanceToCamera));
        // где снег глаже, где грубее — средний слой не одинаков по всему полю
        half rough = saturate(0.6 + SnowNoise(p / 37.0, 217u) * 1.2);
        albedo = lerp(b, a * 0.55 + b * 0.45, nearA) * (SEASON_SNOW_COLOR / SNOW_TEXTURE_MEAN);
        tilt = na.xy * (0.55 * nearA) + nb.xy * (0.8 * midB * rough) + nc.xy * 0.25;
        height = saturate(0.5 + (SeasonLuminance(a) - 0.9) * 14);
    }

    // пятна светлее-темнее и надувы поперёк ветра (~26 м вдоль, ~7 м поперёк, высотой ~0.35 м)
    float macro = SnowNoise(p / 61.0, 201u) * 0.6 + SnowNoise(p / 19.0, 202u) * 0.4;
    albedo *= 1 + macro * 0.12;
    float2 across = float2(-SNOW_WIND.y, SNOW_WIND.x);
    float2 q = float2(dot(p, SNOW_WIND) / 26.0, dot(p, across) / 7.0);
    float d0 = SnowNoise(q, 203u);
    float dAlong = (SnowNoise(q + float2(0.02, 0), 203u) - d0) / (0.02 * 26.0);
    float dAcross = (SnowNoise(q + float2(0, 0.02), 203u) - d0) / (0.02 * 7.0);
    tilt -= (dAlong * SNOW_WIND + dAcross * across) * 0.35;

    // земля сквозь снег: серее над тёмным, чуть её оттенка
    half over = SnowOverGround(ground);
    albedo = lerp(albedo * over, ground * 1.8 + 0.12, (1 - over) * 0.35);

    // накатанный снег дорог: серый, с грязью, ровнее и глаже
    half3 packedColour = SEASON_PACKED_SNOW_COLOR * (1 + SnowNoise(p / 2.3, 204u) * 0.3);
    albedo = lerp(albedo, lerp(packedColour, ground * 1.6 + 0.06, 0.3), packed * 0.8);
    tilt *= 1 - packed * 0.6;
    half smoothness = lerp(0.22, 0.38, packed);

    // искры: в ~3 % клеток по 3.6 см — точка-грань со случайным наклоном, почти зеркальная; только вблизи
    half near = saturate(1 - distanceToCamera / 20) * (1 - packed);
    [branch] if (near > 0)
    {
        float2 cp = p * 28;
        float2 cell = floor(cp);
        float2 centre = float2(SnowHash(cell, 206u), SnowHash(cell, 207u)) * 0.5 + 0.25;
        half sparkle = step(0.97, SnowHash(cell, 205u)) * (1 - smoothstep(0.08, 0.16, length(cp - cell - centre))) * near;
        float angle = SnowHash(cell, 208u) * 6.2831853;
        tilt = lerp(tilt, float2(cos(angle), sin(angle)) * (0.25 + 0.4 * SnowHash(cell, 209u)), sparkle);
        smoothness = lerp(smoothness, 0.96, sparkle);
    }

    SnowSurface s;
    s.albedo = albedo;
    s.normalWS = normalize(geometryNormalWS + float3(tilt.x, 0, tilt.y));
    s.smoothness = smoothness;
    s.height = height;
    return s;
}

// Покров снега (0..1). При _SnowCover = 1 — сплошной на всём, что не круче ~45°; меньше — пятна: раньше тает на южных
// склонах (−Z — юг), на тёмной земле и дорогах, край рвётся по текстуре снега и мелкому шуму у камеры. footprint —
// сколько метров земли в пикселе: шире край там, где пиксель накрывает много шума (вдали не искрит).
half SnowCoverAt(float3 positionWS, half3 geometryNormalWS, half3 ground, half packed, half height, float distanceToCamera,
    float footprint)
{
    half flat = smoothstep(0.55, 0.85, geometryNormalWS.y);

    if (_SnowCover >= 0.999)
        return flat;

    float2 p = positionWS.xz;
    float2 warp = float2(SnowNoise(p / 47.0, 211u), SnowNoise(p / 47.0 + 31.7, 212u)) * 18;
    // пятна вытянуты по ветру зимы (снег лежит надувами), а не круглые
    float2 drift = p + warp;
    drift = float2(dot(drift, SNOW_WIND) / 1.7, dot(drift, float2(-SNOW_WIND.y, SNOW_WIND.x)));
    half nearDetail = saturate(1 - distanceToCamera / 400);
    float field = 0.5 + SnowNoise(drift / 31.0, 213u) * 0.7 + SnowNoise(drift / 11.0 + 5.3, 214u) * 0.35 +
                  SnowNoise(p / 3.9, 215u) * 0.15 * nearDetail;
    // к сплошному покрову таяние слабеет: на 100 % снег лежит везде
    half melt = saturate((1 - _SnowCover) * 2.5);
    field -= (max(0, -geometryNormalWS.z) * 1.6 + (1 - smoothstep(0.03, 0.2, SeasonLuminance(ground))) * 0.12 + packed * 0.12) * melt;
    half nearEdge = saturate(1 - distanceToCamera / 60);
    field += ((height - 0.5) * 0.1 + SnowNoise(p / 0.7, 216u) * 0.12) * nearEdge;
    float threshold = 1.0 - _SnowCover * 1.1;
    // крутизна поля: у камеры его рвёт мелкий шум (~0.3 на метр), вдали — крупный (~0.04 на метр)
    float edge = max(footprint * lerp(0.04, 0.3, nearEdge), 0.015);
    // у края пятна снег тонкий и рваный — сквозь него торчит трава (мелкий шум у камеры, вдали край просто мягкий)
    half band = smoothstep(threshold - 0.09 - edge, threshold + 0.03 + edge, field);
    half detail = 0.5 + (SnowNoise(p / 0.35, 218u) * 0.6 + SnowNoise(p / 1.3, 219u) * 0.4) * nearEdge;
    return saturate(band * 1.5 - (1 - detail)) * flat;
}

#endif
