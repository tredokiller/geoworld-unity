// Пора года (Seasons.cs) для шейдеров земли, воды и растений: _Season — x весна, y лето, z осень, w зима (одна единица;
// ноль везде — ещё не задана, как лето), _SnowCover — снег на земле зимой (0..1, покров из пресета).
#ifndef GAME_SEASONS_INCLUDED
#define GAME_SEASONS_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"

float4 _Season;
float _SnowCover;

// Цвет снега (средний, линейный; свежий снег отражает ~85 %) и накатанного снега на дорогах.
#define SEASON_SNOW_COLOR half3(0.86, 0.875, 0.91)
#define SEASON_PACKED_SNOW_COLOR half3(0.62, 0.62, 0.64)

half SeasonLuminance(half3 colour)
{
    return dot(colour, half3(0.299, 0.587, 0.114));
}

// Насколько цвет — зелень (0..1): зелёный канал выше красного и синего.
half SeasonGreenness(half3 colour)
{
    return saturate((colour.g - max(colour.r, colour.b)) / max(colour.g, 0.02) * 3);
}

// Убранное поле летом: снимок — раннего лета, на пашне (field — доля пашни по карте земель, B карты _PuddleLand) посев на
// нём зелёный, а летом игры поле убрано, и на нём стерня (её карточки GroundDressing сажает там же). Земля под ней
// соломенная: иначе карточки — золотой круг в 60 м вокруг дрона на зелёном поле, а в просветах между стеблями — газон.
// Тон — бежево-соломенный при яркости снимка, поднятой к стерне (~#837B69 при средней зелени посева), поэтому узор
// снимка — полосы, следы техники — остаётся; ровная заливка цветом карточек читалась рыжим песком. Пахота и жёлтое на
// снимке — как есть.
half3 SeasonHarvest(half3 albedo, half field)
{
    half greenness = (albedo.g - max(albedo.r, albedo.b)) / max(albedo.g, 0.02);
    // не порогом по зелени: блоки сжатия снимка (4–8 м) по зелени скачут, и поле шло квадратами соломы и пахоты — бурому
    // в поле тоже половина соломы, зелёному — вся
    half harvested = field * lerp(0.5, 1.0, smoothstep(-0.02, 0.12, greenness)) * _Season.y;
    half3 straw = min(SeasonLuminance(albedo) * half3(2.5, 2.2, 1.55), half3(0.42, 0.37, 0.26));
    return lerp(albedo, straw, harvested);
}

// Плавный шум 0..1 по миру: в узлах решётки — хеш, между ними — сглаженно.
float SeasonNoise(float2 p, uint salt)
{
    float2 i = floor(p);
    float2 f = p - i;
    f = f * f * (3 - 2 * f);
    int2 c = (int2)i;
    float a = GenerateHashedRandomFloat(uint3(asuint(c.x), asuint(c.y), salt));
    float b = GenerateHashedRandomFloat(uint3(asuint(c.x + 1), asuint(c.y), salt));
    float d = GenerateHashedRandomFloat(uint3(asuint(c.x), asuint(c.y + 1), salt));
    float e = GenerateHashedRandomFloat(uint3(asuint(c.x + 1), asuint(c.y + 1), salt));
    return lerp(lerp(a, b, f.x), lerp(d, e, f.x), f.y);
}

// Лес на снимке не по поре: кроны на нём — тёмная сочная зелень (зелень (G − max(R, B))/G: лес ≈ 0.58, луг ≈ 0.27), и
// осенью SeasonGround делал их оливковыми, а деревья над ними рыжие. Осенью кроны снимка берут цвета осенних деревьев
// (SeasonTrees.hlsl: от поздней жёлто-зелёной через жёлтую и рыжую к бурой) пятнами по ~15 м, как группы крон, а там,
// где деревья голые (~30 %), — бурую подстилку сквозь ветки. Рисунок снимка (яркость) остаётся. Вблизи под деревьями
// землю потом всё равно кладёт лесная подстилка (ForestFloor.hlsl). Зимой — как было (SeasonGround: солома): по этой земле
// решает снег, а тёмная — «тает первой».
half3 SeasonForestCanopy(half3 albedo, float2 xz)
{
    half greenness = (albedo.g - max(albedo.r, albedo.b)) / max(albedo.g, 0.02);
    half canopy = smoothstep(0.36, 0.5, greenness) * _Season.z;

    [branch] if (canopy > 0.001)
    {
        half luminance = SeasonLuminance(albedo);
        half hue = saturate((SeasonNoise(xz / 15.0, 151u) * 0.75 + SeasonNoise(xz / 4.5, 152u) * 0.25 - 0.2) * 1.65) * 3;
        half3 leaves = hue < 1 ? lerp(half3(1.0, 1.1, 0.45), half3(1.45, 1.2, 0.3), hue) :
                       hue < 2 ? lerp(half3(1.45, 1.2, 0.3), half3(1.6, 0.85, 0.25), hue - 1) :
                                 lerp(half3(1.6, 0.85, 0.25), half3(1.25, 0.6, 0.3), hue - 2);
        half bare = smoothstep(0.6, 0.7, SeasonNoise(xz / 21.0, 153u));
        albedo = lerp(albedo, luminance * lerp(leaves, half3(1.5, 0.95, 0.6), bare), canopy * 0.8);
    }

    return albedo;
}

// Зелень земли по поре: осенью желтеет к оливковой и соломенной, весной сочнее, зимой бурая (трава спит, снега может не
// быть). Летом — как на снимке.
half3 SeasonGround(half3 albedo)
{
    half green = SeasonGreenness(albedo);
    half luminance = SeasonLuminance(albedo);
    albedo = lerp(albedo, lerp(albedo, luminance * half3(1.4, 1.15, 0.65), 0.55), green * _Season.z);
    albedo = lerp(albedo, albedo * half3(1.02, 1.12, 0.9), green * _Season.x);
    // зимняя трава — светлая сухая солома, не чернота (между пятнами снега земля была почти чёрной)
    albedo = lerp(albedo, lerp(albedo, luminance * half3(1.9, 1.65, 1.25), 0.8), green * _Season.w);
    return albedo;
}

#endif
