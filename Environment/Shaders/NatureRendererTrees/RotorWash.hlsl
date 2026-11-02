// Поток от винтов дрона у земли: трава, цветы и низкие кусты клонятся от точки под дроном, прижимаются и треплются.
// DroneRotorWash задаёт _RotorWashPosition (xyz — точка на земле под дроном, w — радиус, м) и _RotorWashStrength (0..1).
// Подключается во все проходы NM_Foliage (Nature Renderer) перед VertexDescriptionFunction: тени и глубина гнутся так же.
// Сперва растение оседает в лужу или болотную воду (PuddleSink.hlsl), если стоит в ней.
#ifndef ROTOR_WASH_INCLUDED
#define ROTOR_WASH_INCLUDED

// тени травы гаснут плавно (NatureRendererFade.hlsl) — только в NM_Foliage, который включает этот файл
#define GAME_GRASS_SHADOW_FADE 1

#include "PuddleSink.hlsl"
#include "SeasonFoliage.hlsl"
#include "Packages/com.tredokiller.geoworld/Environment/Shaders/FieldWind.hlsl"

// Растение у земли под волнами ветра (трава, посевы, стерня, камыш, цветы; не деревья, не кусты точки взлёта, не опад).
bool FieldWindPlant()
{
    return _PuddleSink > 0.01 && _RotorWashClear < 0.5;
}

// Волна порыва (FieldWind.hlsl): наклон по ветру; стерня (_SeasonKeep 2) жёсткая — клонится вчетверо меньше.
float3 FieldWindBendOS(float3 positionOS)
{
    [branch] if (!FieldWindPlant())
        return positionOS;

    float3 positionWS = TransformObjectToWorld(positionOS);
    float3 pivotWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
    return TransformWorldToObject(FieldWindBend(positionWS, pivotWS, _SeasonKeep > 1.5 ? 0.25 : 1.0));
}

// Цвет растения в волне — светлее (проход вперёд, по мировой точке пикселя).
half3 FieldWindColour(half3 colour, float3 positionWS)
{
    [branch] if (!FieldWindPlant())
        return colour;

    return FieldWindSheen(colour, positionWS.xz, _SeasonKeep > 1.5 ? 0.35 : 1.0);
}

float4 _RotorWashPosition;
float _RotorWashStrength;

// Опавшие листья (материал с _RotorWashClear = 1), которые поток уже сдул. RotorWashLeaves ведёт маску вокруг дрона:
// r — какая доля листьев с клочка земли улетела, gb — куда дуло. Лист сдвигается по потоку и уходит под землю, а вместо
// него летят листья-частицы. _RotorWashLeafRect: xy — угол маски в мире (xz), z — 1 / её размер (м), w — 1, если маска есть.
float4 _RotorWashLeafRect;
TEXTURE2D(_RotorWashLeafMask);
SAMPLER(sampler_RotorWashLeafMask);

float3 RotorWashBlowAway(float3 positionOS)
{
    [branch] if (_RotorWashClear < 0.5 || _RotorWashLeafRect.w < 0.5)
        return positionOS;

    float3 positionWS = TransformObjectToWorld(positionOS);
    float2 uv = (positionWS.xz - _RotorWashLeafRect.xy) * _RotorWashLeafRect.z;

    [branch] if (any(uv <= 0.0) || any(uv >= 1.0))
        return positionOS;

    // по вершинам, а не по корню куста: из клочка листьев в пару метров сдувает только ту часть, где прошёл поток
    float4 mask = SAMPLE_TEXTURE2D_LOD(_RotorWashLeafMask, sampler_RotorWashLeafMask, uv, 0);

    [branch] if (mask.r <= 0.004)
        return positionOS;

    float2 direction = mask.gb * 2.0 - 1.0;
    positionWS.xz += direction * (0.4 * smoothstep(0.0, 0.6, mask.r));
    positionWS.y -= 0.3 * smoothstep(0.3, 0.9, mask.r);
    return TransformWorldToObject(positionWS);
}

// Растения разного роста (материал с _SizeVariation, тростник болот): Nature Renderer берёт у деталей террейна только
// наибольшие ширину и высоту, и все растения вида выходили одного размера. Растение уменьшается от корня — пятнами по
// ~6 м (заросль ниже, заросль выше) и каждое своё: от полного (наибольший размер детали) до 1 − _SizeVariation; высоких
// больше, малые — пятнами и поодиночке.
float3 SizeVariation(float3 positionOS)
{
    [branch] if (_SizeVariation <= 0.001)
        return positionOS;

    float3 pivotWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
    float patch = PuddleNoise(pivotWS.xz / 6.0 + 91.7);
    // своё у каждого — по корню с точностью до 2 см
    float own = GenerateHashedRandomFloat(uint3(asuint((int)floor(pivotWS.x * 50.0)), asuint((int)floor(pivotWS.z * 50.0)), 23u));
    float smaller = saturate(patch * 0.6 + own * 0.4);
    return positionOS * (1.0 - _SizeVariation * smaller * sqrt(smaller));
}

float3 RotorWashBend(float3 positionOS)
{
    positionOS = FieldWindBendOS(RotorWashBlowAway(PuddleSink(SizeVariation(positionOS))));

    [branch] if (_RotorWashStrength <= 0.001)
        return positionOS;

    float3 positionWS = TransformObjectToWorld(positionOS);
    float3 pivotWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
    float2 away = pivotWS.xz - _RotorWashPosition.xz;
    float distance = length(away);
    float falloff = saturate(1.0 - distance / max(_RotorWashPosition.w, 0.01));
    // только низкие растения: кроны деревьев выше пары метров не трогаем
    float height = max(0.0, positionWS.y - pivotWS.y);
    // линейный спад: при квадратичном трава в кадре FPV-камеры (с ~2.3 м от дрона) почти не клонилась
    float push = _RotorWashStrength * falloff * (1.0 - smoothstep(1.2, 2.2, height));

    [branch] if (push <= 0.0001)
        return positionOS;

    float2 direction = distance > 0.001 ? away / distance : float2(1.0, 0.0);
    float flutter = 0.7 + 0.3 * sin(_Time.y * 23.0 + distance * 4.0 + pivotWS.x * 1.7 + pivotWS.z * 2.3);
    float bend = push * flutter * height;
    // дрожь в потоке: заметна уже на холостых оборотах, когда наклон ещё мал (корень — быстрее растёт на слабом потоке)
    float tremble = sqrt(push) * 0.15 * height * sin(_Time.y * 31.0 + pivotWS.x * 3.1 + pivotWS.z * 2.7);
    // колыхание: медленнее дрожи и шире — с арма, на холостых, трава ходит волнами, а не только мелко дрожит
    float sway = sqrt(push) * 0.15 * height * sin(_Time.y * 9.0 + distance * 1.5 + pivotWS.x * 0.9 + pivotWS.z * 1.3);
    positionWS.xz += direction * (bend * 0.9 + tremble * 0.5 + sway) + float2(-direction.y, direction.x) * (tremble + sway * 0.4);
    positionWS.y -= bend * 0.45;
    return TransformWorldToObject(positionWS);
}

#endif
