// Земля вокруг руин (война): по карте руин вокруг камеры (RuinMap.cs, _RuinMap: R пыль и крошка, G доля кирпича, B обломки
// у стены, A внутри контура). Серо-бежевая цементная и штукатурная пыль поверх земли, ближе к стене — крошка бетона и
// кирпича (красного у домов, силикатного), у самой стены разрушенного — тёмная россыпь обломков; край рваный по шуму, рисунок
// — слоёв фактуры земли (_RuinLayers из GroundDetail: гравий, галька, камень) под своими цветами. Ни копоти, ни гари: бой
// окончен (правило). Голая: в дождь темнеет и блестит, как голая земля — серая грязь. Своих сэмплеров нет: карта — сэмплером
// sampler_LinearClamp, массивы — сэмплером снега (GroundDetail.hlsl). Нужны Puddles.hlsl и GroundDetail.hlsl до этого файла.
#ifndef GAME_GROUND_RUINS_INCLUDED
#define GAME_GROUND_RUINS_INCLUDED

TEXTURE2D(_RuinMap);
// xy — начало окна в мире (x, z), z — 1/размер окна, w — текселей по стороне (0 — карты нет)
float4 _RuinMapRect;
// x — гравий (пыль и бетонная крошка), y — галька (кирпич), z — камень (обломки), w — плитка (номера слоёв фактуры земли)
float4 _RuinLayers;

void GroundRuins(inout InputData inputData, float2 dpdx, float2 dpdy, inout half3 albedo, inout half smoothness, inout half occlusion, inout half bare)
{
    [branch] if (_RuinMapRect.w < 0.5)
        return;

    float3 positionWS = inputData.positionWS;
    float2 uv = (positionWS.xz - _RuinMapRect.xy) * _RuinMapRect.z;
    float2 edge = min(uv, 1 - uv);
    half fade = saturate(min(edge.x, edge.y) * 32);

    [branch] if (fade <= 0)
        return;

    half4 m = SAMPLE_TEXTURE2D_LOD(_RuinMap, sampler_LinearClamp, uv, 0);
    m.rgb *= fade;

    [branch] if (m.r < 0.004)
        return;

    // рваный край: пыль ложится пятнами, не кольцом по контуру
    half n = PuddleNoise(positionWS.xz / 3.3 + 17.3) * 0.6 + PuddleNoise(positionWS.xz / 0.9 + 3.7) * 0.4;
    half cover = saturate(m.r * 1.35 - n * 0.55 + 0.12);
    cover = cover * cover * (3 - 2 * cover);
    half rubble = saturate(m.b * 1.4 - n * 0.45 + 0.1);

    [branch] if (cover <= 0.002)
        return;

    GroundDetailSample grit = SampleGroundDetailLayer(_RuinLayers.x, positionWS.xz, dpdx, dpdy);
    GroundDetailSample bricks = SampleGroundDetailLayer(_RuinLayers.y, positionWS.xz * 1.3 + 5.1, dpdx * 1.3, dpdy * 1.3);
    GroundDetailSample chunks = SampleGroundDetailLayer(_RuinLayers.z, positionWS.xz * 0.7 + 2.3, dpdx * 0.7, dpdy * 0.7);

    // цвета (линейные): пыль, бетонная крошка, кирпич красный и силикатный, обломки в тени друг друга
    const half3 dustTone = half3(0.30, 0.285, 0.26);
    const half3 concreteTone = half3(0.22, 0.21, 0.19);
    const half3 redBrick = half3(0.25, 0.115, 0.07);
    const half3 whiteBrick = half3(0.30, 0.29, 0.26);
    const half3 rubbleTone = half3(0.15, 0.14, 0.13);

    half brickShare = saturate(m.g * 1.2 + (PuddleNoise(positionWS.xz / 0.45 + 41.0) - 0.5) * 0.7);
    half3 brickTone = lerp(whiteBrick, redBrick, saturate(m.g * 1.3));
    half3 crumbs = lerp(concreteTone * grit.ratio, brickTone * bricks.ratio, brickShare * 0.75);
    half3 dust = lerp(albedo, dustTone * lerp(1, grit.ratio, 0.5), 0.8);
    // ближе к стене крошка, дальше — пыль поверх земли
    half3 ruin = lerp(dust, crumbs, saturate(m.r * 1.1 - 0.15));
    ruin = lerp(ruin, rubbleTone * chunks.ratio, rubble * 0.8);
    albedo = lerp(albedo, ruin, cover);
    smoothness = lerp(smoothness, 0.06, cover);
    occlusion *= lerp(1, lerp(grit.ao, chunks.ao, rubble), cover);
    bare = max(bare, cover);

    // рельеф крошки и обломков — в плоскости XZ мира, как у фактуры земли
    half3 normalTS = normalize(lerp(grit.normalTS, chunks.normalTS, rubble));
    normalTS.xy *= cover;
    float3 normalWS = inputData.normalWS;
    float3 tangentWS = normalize(cross(normalWS, float3(0, 0, 1)));
    float3 bitangentWS = cross(tangentWS, normalWS);
    inputData.normalWS = normalize(tangentWS * normalTS.x + bitangentWS * normalTS.y + normalWS * normalTS.z);
}

#endif
