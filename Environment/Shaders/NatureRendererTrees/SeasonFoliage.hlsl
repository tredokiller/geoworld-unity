// Растения NM_Foliage (Nature Renderer) по поре года. Роль материала — по его свойствам:
//  - листва деревьев: _PuddleSink = 0 и не опавшие листья (SeasonTrees.hlsl). У голого дерева (зимой все, осенью часть)
//    от листвы остаются ветки и прутья: в атласах листвы бука и тополя есть и ветки с листьями, и голые прутья — зелёное
//    отбрасываем (SeasonFoliageAlpha), бурое и серое оставляем; раньше вершины листвы уходили в корень целиком, и вместе
//    с листьями пропадали все мелкие ветки — дерево стояло сухим скелетом. Вдали, где прутья тоньше пикселя, на месте
//    листьев — редкая серо-бурая сетка прутьев, как у дальних карточек (SeasonCross.hlsl). Осенью у дерева свой цвет,
//    весной молодая листва;
//  - трава, цветы, тростник (_PuddleSink > 0): осенью сохнут, зимой — сухая солома (из-под снега торчит тростник, остальное
//    траву зимой не сажает GroundDressing), весной свежая зелень;
//  - опавшие листья (_RotorWashClear = 1): осенью у каждой кучки свой цвет — жёлтый, рыжий, ржавый, бурый, изредка
//    красный; летом — тёмно-бурые прошлогодние, весной — выцветшие серо-бурые, зимой — мокрые тёмно-бурые; яркость — в тон
//    лесной подстилки (ForestFloor.hlsl);
//  - всходы озимых (_SeasonKeep = 1): зелёные круглый год, зимой тусклее;
//  - сухое (_SeasonKeep = 2: стерня, ветошь): солома круглый год, зимой серее.
// Подключается из RotorWash.hlsl — во все проходы; лист или нет — по той же выборке текстуры, что и альфа прохода, поэтому
// глубина, тени и цвет прорежены одинаково.
#ifndef GAME_SEASON_FOLIAGE_INCLUDED
#define GAME_SEASON_FOLIAGE_INCLUDED

#include "Packages/com.tredokiller.geoworld/Environment/Shaders/NatureRendererTrees/SeasonTrees.hlsl"

bool SeasonIsTreeLeaves()
{
    return _PuddleSink <= 0.001 && _RotorWashClear < 0.5;
}

// Альфа листвы голого дерева: листья прочь, ветки и прутья атласа остаются. Прутья атласа тоньше пикселя, когда в пикселе
// больше ~8 его текселей (десятки метров) — оттуда на месте листьев проступает сетка прутьев. Тексели — самой карты
// материала (бук и тополь 4096, сгенерированный дуб 2048: с числом 4096 у дуба сетка вставала вдвое ближе).
half SeasonFoliageAlpha(half alpha, half3 texel, float2 uv)
{
    float2 footprint = max(abs(ddx(uv)), abs(ddy(uv)));

    [branch] if (!SeasonIsTreeLeaves() || !SeasonTreeBare())
        return alpha;

    float2 texels = footprint * _BaseColorMap_TexelSize.zw * _Tiling_and_Offset.xy;
    half haze = SeasonTwigHaze(uv, footprint) * smoothstep(4.0, 16.0, max(texels.x, texels.y));
    return alpha * lerp(1.0, haze, SeasonLeafShare(texel));
}

half3 SeasonFoliageColour(half3 colour)
{
    half luminance = SeasonLuminance(colour);

    // опавшие листья: текстура — атлас листьев тополя (рыжие, жёлтые, бурые, есть зелёные; линейная средняя
    // 0.29, 0.18, 0.10, яркость ~0.21 — в 2–3 раза светлее лесной подстилки под ними, ForestFloor). Цвет — от яркости
    // текстуры с тоном поры: осенью у каждой кучки свой (по её корню — листья разных деревьев): жёлтый (клён, тополь,
    // липа), рыжий, ржавый (бук, дуб), бурый, изредка красный (клён), ярче подстилки на треть — свежий лист сверху; летом
    // — тёмно-бурые прошлогодние (зелёные на бурой подстилке были чужими), весной выцветшие серо-бурые, зимой на голой
    // земле между сугробами — мокрые тёмно-бурые (рыжие пятна на снегу читались мусором). Пёстрость самой текстуры
    // остаётся долей: листья в кучке не одного тона.
    if (_RotorWashClear > 0.5)
    {
        float pick = SeasonTreeRandom(47u);
        half shade = lerp(0.85, 1.15, SeasonTreeRandom(48u));
        half3 autumn = pick < 0.22 ? half3(0.68, 0.54, 0.13) :
                       pick < 0.47 ? half3(0.76, 0.39, 0.1) :
                       pick < 0.67 ? half3(0.63, 0.28, 0.1) :
                       pick < 0.92 ? half3(0.46, 0.3, 0.16) :
                                     half3(0.73, 0.21, 0.1);
        half summerShare = saturate(1 - _Season.x - _Season.z - _Season.w);
        half3 tone = autumn * _Season.z + half3(0.42, 0.33, 0.22) * summerShare + half3(0.5, 0.42, 0.3) * _Season.x +
                     half3(0.34, 0.25, 0.17) * _Season.w;
        return lerp(colour * 0.45, luminance * tone * shade, lerp(0.92, 0.8, _Season.z));
    }

    // сухое (_SeasonKeep = 2: стерня, прошлогодняя ветошь) — солома круглый год: весной не зеленеет, осенью не желтеет,
    // зимой сереет от сырости (как трава, оно становилось золотой щетиной над снегом)
    if (_SeasonKeep > 1.5)
        return lerp(colour, luminance * half3(0.95, 0.92, 0.86), 0.5 * _Season.w);

    // всходы озимых (_SeasonKeep = 1): осенью и под снегом зелёные, зимой лишь тусклее от мороза
    if (_SeasonKeep > 0.5)
        return lerp(colour, colour * half3(0.85, 0.85, 0.75), _Season.w);

    if (_PuddleSink > 0.001)
    {
        colour = lerp(colour, luminance * half3(1.3, 1.05, 0.6), 0.55 * _Season.z);
        colour = lerp(colour, luminance * half3(1.6, 1.4, 1.0), 0.85 * _Season.w);
        return lerp(colour, colour * half3(1.0, 1.12, 0.85), _Season.x);
    }

    [branch] if (SeasonTreeBare())
        return SeasonBareColour(colour);

    return SeasonTreeLeafColour(colour);
}

#endif
