// Война на земле (WarMap.cs, _WarMap: посечённость места 0..1 в окне 8192 м вокруг камеры, 8 м на тексель, читается LOAD
// — без сэмплера; _WarTreesOn 0 — боёв нет): колеи техники по полям и лугам. Процедурно по мировым метрам, не со снимков
// (правило: колеи и окопы не обводятся по снимку): клетки 200 м, в каждой до двух следов (по 70 %) длиной 80–190 м (не длиннее
// клетки: след из соседней клетки всегда найден, без обрывов на границах клеток) под своим углом, с плавной волной в 3–8 м;
// гусеничный (колея 0.58 м, между колеями 2.8 м) или колёсный (0.32 м, 1.9 м), свежий или почти заросший. Колея —
// укатанная земля заметно темнее, по зелени — голая; по краям — выдавленный светлее грунт, между колеями — примятая полоса;
// уклоны стенок — нормалью. Видна сильнее, где посечённость больше; пиксель шире колеи — она гаснет к средней темноте (видна линией
// издалека, без мерцания). Зовётся после пыли руин (машины шли и по ней), до воронок и снега (снег её покрывает); на
// полотне дорог её нет (road — доля полотна). Нужен Puddles.hlsl.
#ifndef GAME_GROUND_WAR_INCLUDED
#define GAME_GROUND_WAR_INCLUDED

TEXTURE2D(_WarMap);
float4 _WarMapRect;   // x0, z0 окна, 1 / его размер, текселей по стороне
float _WarTreesOn;
// 1 — колеи выключены (для сравнения; глобальная, по умолчанию 0)
float _GroundTracksOff;

half GroundWarAt(float2 xz)
{
    [branch] if (_WarTreesOn < 0.5)
        return 0;

    float2 uv = (xz - _WarMapRect.xy) * _WarMapRect.z;

    [branch] if (any(uv < 0.0) || any(uv >= 1.0))
        return 0;

    return LOAD_TEXTURE2D(_WarMap, (int2)(uv * _WarMapRect.w)).r;
}

// the same map read bilinearly (sampler_LinearClamp, the pass's own) for the effects on the ground: read texel by texel
// its 8 m steps came out as squares in the strength of the tracks and the mud (the trees read it by LOAD, for their CPU twin)
half GroundWarSmooth(float2 xz)
{
    [branch] if (_WarTreesOn < 0.5)
        return 0;

    float2 uv = (xz - _WarMapRect.xy) * _WarMapRect.z;

    [branch] if (any(uv < 0.0) || any(uv >= 1.0))
        return 0;

    return SAMPLE_TEXTURE2D_LOD(_WarMap, sampler_LinearClamp, uv, 0).r;
}

float GroundWarHash(float2 cell, uint salt)
{
    return GenerateHashedRandomFloat(uint3(asuint((int)cell.x), asuint((int)cell.y), salt));
}

// производная smoothstep(a, b, x) по x
float GroundWarSmoothStepD(float a, float b, float x)
{
    float t = saturate((x - a) / (b - a));
    return 6.0 * t * (1.0 - t) / (b - a);
}

void GroundTracks(float3 positionWS, float2 dpdx, float2 dpdy, half greenness, half road, half litter, inout half3 albedo, inout float3 normalWS)
{
    [branch] if (_WarTreesOn < 0.5 || _GroundTracksOff > 0.5)
        return;

    float2 xz = positionWS.xz;
    half war = GroundWarSmooth(xz);
    float footprint = max(length(dpdx), length(dpdy));

    [branch] if (war < 0.15 || footprint > 2.0)
        return;

    const float cellSize = 200.0;
    float2 home = floor(xz / cellSize);
    half rut = 0, berm = 0, band = 0;
    float tilt = 0;
    float2 tiltDirection = float2(1, 0);

    for (int k = 0; k < 18; k++)
    {
        // клетки 3 × 3 вокруг и два следа в каждой (соль следа — в хешах)
        float2 c = home + float2(k % 3 - 1, (k / 3) % 3 - 1);
        uint s = (uint)(k / 9) * 1000u;
        float carries = GroundWarHash(c, 301u + s);

        if (carries > 0.7)
            continue;

        float2 start = (c + float2(GroundWarHash(c, 303u + s), GroundWarHash(c, 307u + s))) * cellSize;
        float angle = GroundWarHash(c, 311u + s) * 6.2831853;
        float2 along = float2(cos(angle), sin(angle));
        float2 side = float2(-along.y, along.x);
        float run = 80.0 + 110.0 * GroundWarHash(c, 313u + s);
        float2 rel = xz - start;
        float t = dot(rel, along);

        if (t < -3.0 || t > run + 3.0)
            continue;

        // плавная волна по ходу: машина не идёт по линейке
        float period = 35.0 + 40.0 * GroundWarHash(c, 317u + s), amplitude = 3.0 + 5.0 * GroundWarHash(c, 331u + s);
        float wave = sin(t / period + carries * 20.0) * amplitude;
        float slopeOfWave = cos(t / period + carries * 20.0) * amplitude / period;
        float off = dot(rel, side) - wave;
        float ends = smoothstep(-3.0, 8.0, t) * (1.0 - smoothstep(run - 8.0, run + 3.0, t));
        bool tracked = GroundWarHash(c, 337u + s) < 0.6;
        float gauge = tracked ? 1.4 : 0.95;
        float halfWidth = tracked ? 0.29 : 0.16;
        float age = 0.35 + 0.65 * GroundWarHash(c, 347u + s);
        float soft = max(footprint, 0.05);
        float d = abs(abs(off) - gauge);
        // уже пикселя — к средней темноте (доля пикселя под колеёй)
        float coverage = saturate(2.0 * halfWidth / footprint);
        half inRut = (1.0 - smoothstep(halfWidth - soft, halfWidth + soft, d)) * ends * age * coverage;
        half edge = smoothstep(halfWidth - soft, halfWidth + soft, d) * (1.0 - smoothstep(halfWidth + 0.08, halfWidth + 0.4 + soft, d)) * ends * age * coverage;
        // примятая полоса между колеями и по ним
        band = max(band, (1.0 - smoothstep(gauge + halfWidth - soft, gauge + halfWidth + soft, abs(off))) * ends * age);

        if (inRut + edge * 0.5 <= rut + berm * 0.5)
            continue;

        rut = inRut;
        berm = edge;
        // уклон стенок колеи поперёк следа: профиль −0.08 в колее, +0.03 на валике; d по off: знак стороны
        float dd = sign(abs(off) - gauge) * sign(off);
        float rise = (0.08 * GroundWarSmoothStepD(halfWidth - soft, halfWidth + soft, d) - 0.03 * GroundWarSmoothStepD(halfWidth + 0.08, halfWidth + 0.4 + soft, d)) * dd;
        tilt = rise * ends * age * saturate(0.25 / soft);
        float2 across = normalize(side - along * slopeOfWave);
        tiltDirection = across;
    }

    [branch] if (rut + berm + band < 0.003)
        return;

    // fading out with distance (not cut at 2 m a pixel), none on carriageways and under the trees (the forest floor)
    half strength = smoothstep(0.15, 0.6, war) * (1.0 - road) * (1.0 - litter) * (1.0 - smoothstep(1.0, 2.0, footprint));

    half green = saturate(greenness * 4.0);
    // в колее: укатанная земля темнее; по зелени — голая
    half3 soil = half3(0.12, 0.10, 0.08);
    albedo = lerp(albedo, albedo * 0.8, band * strength * 0.7);
    half3 inside = lerp(albedo * 0.45, soil, green * 0.7);
    albedo = lerp(albedo, inside, rut * strength);
    // валик выдавленного грунта светлее, по зелени — примятая трава желтее
    albedo = lerp(albedo, albedo * half3(1.15, 1.12, 1.02), berm * strength * 0.7);
    normalWS = normalize(normalWS - float3(tiltDirection.x, 0, tiltDirection.y) * (tilt * strength));
}

#endif
