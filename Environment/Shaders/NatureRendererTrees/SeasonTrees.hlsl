// Деревья по поре года (Seasons.hlsl) — общее для листвы вблизи (NM_Foliage) и карточек вдали (NM_Cross): у каждого дерева
// свой случай по его корню, поэтому вблизи и вдали дерево одинаковое. Лиственные (бук, тополь, дуб, ива, клён): зимой голые
// все, осенью голая примерно треть, у остальных свой цвет — от жёлто-зелёного до рыжего и бурого. Хвойные (сосна; у материала
// _Evergreen = 1, свойство есть в NM_Foliage и NM_Cross): хвоя круглый год, осенью без расцветки, зимой темнее и тусклее,
// весной чуть свежее (светлые молодые побеги).
#ifndef GAME_SEASON_TREES_INCLUDED
#define GAME_SEASON_TREES_INCLUDED

#include "Packages/com.tredokiller.geoworld/Environment/Shaders/Seasons.hlsl"

// Случайное 0..1 дерева — по корню (с точностью до 10 см).
float SeasonTreeRandom(uint salt)
{
    float3 pivot = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
    return GenerateHashedRandomFloat(uint3(asuint((int)floor(pivot.x * 10.0)), asuint((int)floor(pivot.z * 10.0)), salt));
}

bool SeasonTreeBare()
{
    return _Evergreen < 0.5 && (_Season.w > 0.5 || (_Season.z > 0.5 && SeasonTreeRandom(41u) < 0.3));
}

// Насколько тексель листвы — лист (0 — ветка, кора, сухой лист; 1 — зелёный лист, и в тени тоже).
half SeasonLeafShare(half3 texel)
{
    return smoothstep(0.08, 0.3, SeasonGreenness(texel));
}

// Редкая сетка прутьев на месте листвы голого дерева там, где настоящие прутья тоньше пикселя (1 — пиксель остаётся): ~25 %
// клеток; клетка — 1.5–3 пикселя (степень двойки по размеру пикселя в UV), поэтому сетка стоит на дереве и не мерцает,
// пока дальность не изменится вдвое. footprint — размер пикселя в UV (производные — вне ветвлений).
half SeasonTwigHaze(float2 uv, float2 footprint)
{
    float pixel = max(max(footprint.x, footprint.y), 1e-5);
    float cells = exp2(floor(log2(1.0 / (pixel * 1.5))));
    int2 cell = (int2)floor(uv * cells);
    return GenerateHashedRandomFloat(uint3(asuint(cell.x), asuint(cell.y), 97u)) < 0.25 ? 1.0 : 0.0;
}

// Цвет голой кроны: ветки и кора — как в текстуре, сетка прутьев на месте листьев — серо-бурая.
half3 SeasonBareColour(half3 colour)
{
    return lerp(colour, SeasonLuminance(colour) * half3(0.8, 0.7, 0.62), SeasonLeafShare(colour));
}

// Листва дерева по поре: осенью — его цвет (сохраняя рисунок текстуры), весной — молодая: светлая, салатовая, у каждого
// дерева чуть своя (летняя тёмная зелень от неё заметно отличается).
half3 SeasonTreeLeafColour(half3 colour)
{
    half luminance = SeasonLuminance(colour);

    // хвоя: без осенней расцветки; зимой темнее и серее, весной на концах побегов светлая молодая хвоя
    [branch] if (_Evergreen > 0.5)
    {
        half3 winter = lerp(colour, luminance * half3(0.85, 0.95, 0.9), 0.35) * 0.82;
        colour = lerp(colour, winter, _Season.w);
        return lerp(colour, colour * half3(1.05, 1.12, 0.95), 0.5 * _Season.x);
    }

    // цвет поры — только листьям (зелёным текселям): ветки и кора на карточках и в атласах остаются своими, а у
    // сухостоя (на карточке одни ветки) осень не рыжит серые сучья
    half leaf = SeasonLeafShare(colour);
    float r = SeasonTreeRandom(43u);
    half3 autumn = r < 0.3 ? half3(1.45, 1.2, 0.3) : r < 0.6 ? half3(1.6, 0.85, 0.25) : r < 0.8 ? half3(1.25, 0.6, 0.3) : half3(1.0, 1.1, 0.45);
    colour = lerp(colour, luminance * autumn, 0.75 * _Season.z * leaf);
    half3 spring = luminance * lerp(half3(1.15, 1.7, 0.5), half3(1.35, 1.65, 0.45), SeasonTreeRandom(44u));
    colour = lerp(colour, spring, 0.6 * _Season.x * leaf);
    return colour;
}

#endif
