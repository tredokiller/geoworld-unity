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

    // голое по поре; посечённое войной — листья клеток карточки (1/16: пятна кроны) по его доле (SeasonTrees.hlsl)
    bool bare = SeasonTreeBare();
    half strip = bare ? 0 : SeasonWarStrip();

    [branch] if (!bare && (strip <= 0.0 || !SeasonWarGone(uv, 16.0, strip)))
        return alpha;

    return alpha * lerp(1.0, SeasonTwigHaze(uv, footprint), SeasonLeafShare(texel));
}

// uv: the same as SeasonCrossAlpha's — the cells the war took are coloured like a bare crown (their twig haze grey-brown),
// the rest of the crown dried by its strip
half3 SeasonCrossColour(half3 colour, float2 uv)
{
    [branch] if (SeasonTreeBare())
        return SeasonBareColour(colour);

    half strip = SeasonWarStrip();

    [branch] if (strip >= 0.999 || (strip > 0.0 && SeasonWarGone(uv, 16.0, strip)))
        return SeasonBareColour(colour);

    return SeasonWarLeafColour(SeasonTreeLeafColour(colour), strip);
}

// without uv (no per-cell decision): a stripped crown dried as a whole
half3 SeasonCrossColour(half3 colour)
{
    [branch] if (SeasonTreeBare())
        return SeasonBareColour(colour);

    half strip = SeasonWarStrip();
    return strip >= 0.999 ? SeasonBareColour(colour) : SeasonWarLeafColour(SeasonTreeLeafColour(colour), strip);
}

#endif
