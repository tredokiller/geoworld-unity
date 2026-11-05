// Дороги после боёв (асфальт и плитка, paved — доля полотна из GroundRoad): у воронок полотно разбито на куски —
// по карте воронок (CraterMap: в чаше, на валу мелких, в свежем выбросе), куски асфальта вздыблены и разного тона, между
// ними трещины, часть выбита до грунта (темнее, сырее); где шли бои (WarMap), на полотне натасканная с полей грязь пятнами.
// Рисуется после полотна и до пыли руин и воронок (их чаши темнеют поверх). Нужны GroundWar.hlsl и GroundCraters.hlsl.
#ifndef GAME_GROUND_ROAD_SCARS_INCLUDED
#define GAME_GROUND_ROAD_SCARS_INCLUDED

// Ячейки Вороного 0.4 м: F1, F2 до двух ближайших центров и номер ячейки ближайшего.
void GroundScarCells(float2 p, out float f1, out float f2, out float2 id)
{
    float2 base = floor(p);
    f1 = 8.0;
    f2 = 8.0;
    id = base;

    for (int j = -1; j <= 1; j++)
    for (int i = -1; i <= 1; i++)
    {
        float2 c = base + float2(i, j);
        float2 centre = c + float2(GroundWarHash(c, 401u), GroundWarHash(c, 409u));
        float d = length(p - centre);

        if (d < f1)
        {
            f2 = f1;
            f1 = d;
            id = c;
        }
        else if (d < f2)
            f2 = d;
    }
}

void GroundRoadScars(inout InputData inputData, float2 dpdx, float2 dpdy, half paved, inout half3 albedo, inout half smoothness, inout half occlusion)
{
    [branch] if (paved < 0.01 || _WarTreesOn < 0.5)
        return;

    float2 xz = inputData.positionWS.xz;
    float footprint = max(length(dpdx), length(dpdy));
    half torn = 0;

    [branch] if (_CraterMapRect.w > 0.5)
    {
        float2 uv = (xz - _CraterMapRect.xy) * _CraterMapRect.z;

        [branch] if (all(uv > 0.0) && all(uv < 1.0))
        {
            float4 m = SampleCraterMap(uv);
            // faded over the window's last 16 m, as GroundCraters fades its bowls (no straight edge at the window)
            half inside = saturate(min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y)) * 16.0);
            torn = saturate(saturate(-m.b) * 2.5 + saturate(-m.g / 0.06) * 0.8 + saturate(-m.a) * 0.6) * inside;
        }
    }

    half war = GroundWarSmooth(xz);
    half mud = smoothstep(0.25, 0.8, war) * smoothstep(0.45, 0.8, PuddleNoise(xz / 3.7 + 51.3) * 0.7 + PuddleNoise(xz / 0.9 + 7.1) * 0.3);

    [branch] if (torn < 0.01 && mud < 0.01)
        return;

    torn *= paved;
    mud *= paved;

    [branch] if (torn > 0.01)
    {
        float f1, f2;
        float2 id;
        GroundScarCells(xz / 0.4, f1, f2, id);
        float chunk = GroundWarHash(id, 419u);
        // выбит до грунта — чаще ближе к чаше
        half gone = step(chunk, torn * 0.75);
        half crack = (1.0 - smoothstep(0.0, 0.06 + footprint * 2.5, f2 - f1)) * saturate(torn * 2.0);
        half detail = saturate(1.0 - footprint / 0.4);
        half3 broken = albedo * (0.75 + 0.4 * GroundWarHash(id, 421u));
        half3 earth = half3(0.075, 0.062, 0.05);
        half3 scarred = lerp(broken, earth, gone);
        scarred = lerp(scarred, scarred * 0.35, crack * detail);
        albedo = lerp(albedo, scarred, saturate(torn * 1.5));
        smoothness = lerp(smoothness, gone > 0.5 ? 0.15 : smoothness * 0.8, torn);
        occlusion *= lerp(1.0, 0.7, crack * detail * torn);
        // вздыбленные куски: свой наклон у каждого
        float2 tilt = (float2(GroundWarHash(id, 431u), GroundWarHash(id, 433u)) - 0.5) * 0.5 * (1.0 - gone) * torn * detail;
        inputData.normalWS = normalize(inputData.normalWS + float3(tilt.x, 0, tilt.y));
    }

    [branch] if (mud > 0.01)
    {
        // засохшая грязь с полей; в сырость темнее и блестит
        half wet = saturate(_GroundWetness * 1.5);
        half3 dirt = lerp(half3(0.16, 0.135, 0.105), half3(0.07, 0.06, 0.05), wet);
        albedo = lerp(albedo, dirt, mud * 0.85);
        smoothness = lerp(smoothness, lerp(0.05, 0.4, wet), mud);
    }
}

#endif
