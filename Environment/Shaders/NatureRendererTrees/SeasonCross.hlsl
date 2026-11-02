// Дальние деревья (карточки NM_Cross (Nature Renderer)) по поре года. На карточке листва и ветки — одна картинка: у голого
// дерева остаётся всё, что не лист (ствол и ветви), а на месте листвы — редкая серо-бурая сетка прутьев (SeasonTwigHaze,
// как вдали у веток вблизи: голая крона издали — дымка прутьев, а не листва). У дерева с листвой цвет — как у его листвы
// вблизи (тот же случай по корню, SeasonTrees.hlsl). Подключается после NatureRendererFade.hlsl во все проходы; лист или нет —
// по той же выборке текстуры, что и альфа прохода, поэтому глубина, тени и цвет прорежены одинаково.
#ifndef GAME_SEASON_CROSS_INCLUDED
#define GAME_SEASON_CROSS_INCLUDED

#include "Packages/com.tredokiller.geoworld/Environment/Shaders/NatureRendererTrees/SeasonTrees.hlsl"

float SeasonCrossAlpha(float alpha, float2 uv, half3 texel)
{
    float2 footprint = max(abs(ddx(uv)), abs(ddy(uv)));

    [branch] if (!SeasonTreeBare())
        return alpha;

    return alpha * lerp(1.0, SeasonTwigHaze(uv, footprint), SeasonLeafShare(texel));
}

half3 SeasonCrossColour(half3 colour)
{
    [branch] if (SeasonTreeBare())
        return SeasonBareColour(colour);

    return SeasonTreeLeafColour(colour);
}

#endif
