// Трава, цветы и опавшие листья в луже или болотной воде уходят под воду: растение опускается на глубину воды (у
// материала _PuddleSink — на сколько метров в самой луже), над мутной гладью остаются верхушки. Где вода — решает то же,
// что у террейна (Puddles.hlsl), по корню растения, поэтому трава оседает ровно в его лужах и вместе с ними набирается
// и высыхает. Без этого лужи закрывала трава: видно их было только на вытоптанных местах и за кругом, где траву уже не
// рисуют. Деревья (_PuddleSink = 0) не трогаем. Подключается из RotorWash.hlsl — во все проходы NM_Foliage (Nature
// Renderer), тени и глубина оседают так же.
#ifndef GAME_PUDDLE_SINK_INCLUDED
#define GAME_PUDDLE_SINK_INCLUDED

#include "Packages/com.tredokiller.geoworld/Environment/Shaders/Terrain/Puddles.hlsl"

float3 PuddleSink(float3 positionOS)
{
    [branch] if (_PuddleSink <= 0.001)
        return positionOS;

    float3 pivotWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
    half2 land = PuddleLand(pivotWS);

    [branch] if (_PuddleLevel <= 0.001 && land.g <= 0.004)
        return positionOS;

    // пригодность — как у террейна на «средней» земле (у него зелень поднимает порог, грунт опускает)
    float puddleThreshold = _PuddleLevel > 0.001 ? PuddleThreshold(land.r * 0.95) : 2;
    float threshold = min(puddleThreshold, MarshThreshold(land.g));

    // шум до такого не дорастает — воды здесь нет
    [branch] if (threshold > 0.97)
        return positionOS;

    // по крупным слоям шума: кромку луж трава держит мягкой, мелкий слой ей не нужен (вершин травы — миллионы)
    float field = PuddleFieldCoarse(pivotWS.xz);
    // у кромки растение по щиколотку, дальше в воде — по макушку
    half water = smoothstep(threshold - 0.02, threshold + 0.05, field) * step(0.02, max(land.r, land.g));

    [branch] if (water <= 0.001)
        return positionOS;

    float scale = length(float3(UNITY_MATRIX_M._m01, UNITY_MATRIX_M._m11, UNITY_MATRIX_M._m21));
    float3 positionWS = TransformObjectToWorld(positionOS);
    positionWS.y -= water * _PuddleSink * scale;
    return TransformWorldToObject(positionWS);
}

#endif
