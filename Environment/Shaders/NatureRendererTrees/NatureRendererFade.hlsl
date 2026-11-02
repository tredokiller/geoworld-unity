#ifndef NATURE_RENDERER_FADE_INCLUDED
#define NATURE_RENDERER_FADE_INCLUDED

// Дальний край растений Nature Renderer. С _NatureRendererDistanceControl = 1 Nature Renderer сам пишет перед каждым draw
// _ScaleFade = (R - length, length): R — его дальность этого префаба (настройки, последний LOD, FOV), length — _ScaleFade.y
// материала. Растение растворяется к R от своего пивота — там, где Nature Renderer его и отсекает, без ступеньки.
// Обычные рендереры с этим материалом получают _ScaleFade материала: у травы x = 100000 (не растворяется), у карточек
// деревьев x = 1600 — деревья точки взлёта уходят вместе с лесом тайлов (дальность деревьев NatureRendererSettings 2000 м).
//  - NM_Foliage: Distance_Scale = 1 - saturate((d - start) / length) сжимает растение к пивоту и растворяет альфой —
//    только с ключевым словом _DISTANCEBLEND_ON материала (без него управление ничего не меняет); без управления —
//    прежние _CullFarStart/_CullFarDistance материала от вершины;
//  - NM_Cross (карточки дальних деревьев): NatureRendererFade() сжимает карточку к пивоту, лес за ~1.6 км плавно
//    опускается в спутник вместо обрыва.
// Nature Renderer пишет таким материалам и _WorldSpaceCameraPos своей камеры: в Play в окне Scene свет на растениях
// считается от камеры дрона — оценивать вид растений в Game.

bool NatureRendererControlsFade()
{
    return _NatureRendererDistanceControl > 0.5 && _ScaleFade.y > 0.0;
}

// (откуда, на скольких метрах) растворение: длина — не больше 40 % дальности R. Дальности укорачивает оператор в
// настройках графики (трава до 50 м, цветы тогда 25 м), а длина материала — 20–60 м: иначе растение растворялось бы
// уже у камеры.
float2 NatureRendererScaleFade()
{
    float range = max(_ScaleFade.x + _ScaleFade.y, 0.0);
    float length = max(min(_ScaleFade.y, range * 0.4), 0.01);
    return float2(range - length, length);
}

// Тени травы: Nature Renderer режет их по корню растения на своей дальности теней (20 м) — кольцом, которое заметно
// растёт и съёживается при полёте и снижении. В проходе теней трава (_PuddleSink > 0.01; деревья 0, кусты точки взлёта
// 0.002) сжимается к корню и растворяется так же, как вдали, но с _GrassShadowFade (x — откуда, y — на скольких метрах;
// NatureRendererSettings) и уходит ровно к его срезу. GAME_GRASS_SHADOW_FADE задаёт RotorWash.hlsl (только NM_Foliage:
// у NM_Cross нет _PuddleSink).
float4 _GrassShadowFade;

bool GrassShadowFadeActive()
{
#if defined(GAME_GRASS_SHADOW_FADE) && defined(SHADERPASS) && (SHADERPASS == SHADERPASS_SHADOWCASTER)
    return _PuddleSink > 0.01 && _GrassShadowFade.y > 0.0 && NatureRendererControlsFade() && _GrassShadowFade.x < NatureRendererScaleFade().x;
#else
    return false;
#endif
}

float NatureRendererFadeDistance(float3 positionWS)
{
    float3 from = NatureRendererControlsFade() ? GetAbsolutePositionWS(UNITY_MATRIX_M._m03_m13_m23) : positionWS;
    return distance(from, _WorldSpaceCameraPos);
}

float NatureRendererFadeStart(float fallbackStart)
{
    if (GrassShadowFadeActive())
        return _GrassShadowFade.x;

    return NatureRendererControlsFade() ? NatureRendererScaleFade().x : fallbackStart;
}

float NatureRendererFadeLength(float fallbackLength)
{
    if (GrassShadowFadeActive())
        return _GrassShadowFade.y;

    return NatureRendererControlsFade() ? NatureRendererScaleFade().y : fallbackLength;
}

float NatureRendererFade()
{
    if (!NatureRendererControlsFade())
        return 1.0;

    float distanceToPivot = distance(GetAbsolutePositionWS(UNITY_MATRIX_M._m03_m13_m23), _WorldSpaceCameraPos);
    float2 fade = NatureRendererScaleFade();
    return 1.0 - saturate((distanceToPivot - fade.x) / fade.y);
}

#endif
