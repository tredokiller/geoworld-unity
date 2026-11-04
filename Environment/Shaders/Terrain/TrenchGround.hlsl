// Грунт окопов (GeoTrenchMesh): дно, бровка и брустверы — меш с материалом террейна тайла (GeoTrenchGround включает
// _TRENCH_GROUND на его копии), UV1 — сколько на нём своей почвы окопа (x) и дно ли это (y). Почва — слои фактуры земли
// (_TrenchSoilLayers из GroundDetail: выброшенный грунт, заросший грунт, грязь, чернозём) — их рисунок под своим цветом,
// не под цветом спутника: свежий выброс — палевый лёссовидный суглинок с комьями чернозёма, старый — пятнами (шум) зарос,
// к краю бруствера — вид земли вокруг без шва (x → 0); дно — тёмная сырая грязь, неба над ним мало (стенки), и света
// меньше. Потом, как на всём террейне, — мокрость, сезоны и снег (GroundWetness). Своих сэмплеров нет: массивы фактуры
// читаются сэмплером снега (GroundDetail.hlsl). Нужны Puddles.hlsl, Snow.hlsl и GroundDetail.hlsl до этого файла.
#ifndef GAME_TRENCH_GROUND_INCLUDED
#define GAME_TRENCH_GROUND_INCLUDED

// x — выброшенный грунт, y — он же заросший, z — грязь дна, w — чернозём (номера слоёв фактуры земли)
float4 _TrenchSoilLayers;
// 1 — слои опубликованы (GroundDetail), иначе грунт окопа — вид террейна
float _TrenchSoilOn;

void TrenchGround(inout InputData inputData, half2 soil, inout half3 albedo, inout half smoothness, inout half occlusion)
{
    float3 positionWS = inputData.positionWS;
    // производные — до ветвлений
    float2 dpdx = ddx(positionWS.xz);
    float2 dpdy = ddy(positionWS.xz);
    half share = saturate(soil.x);
    half bottom = saturate(soil.y);

    [branch] if (_TrenchSoilOn < 0.5 || share <= 0.002)
        return;

    // пятна: где грунт голый, где зарос (старый выброс зарастает пятнами, свежий почти весь голый); на дне — грязь везде
    half patch = PuddleNoise(positionWS.xz / 1.9 + 11.3) * 0.6 + PuddleNoise(positionWS.xz / 0.6 + 4.1) * 0.4;
    half bare = lerp(smoothstep(1 - share, 1.35 - share, patch + 0.15), 1, bottom);

    GroundDetailSample loam = SampleGroundDetailLayer(_TrenchSoilLayers.x, positionWS.xz, dpdx, dpdy);
    GroundDetailSample lumps = SampleGroundDetailLayer(_TrenchSoilLayers.w, positionWS.xz * 0.83 + 7.1, dpdx * 0.83, dpdy * 0.83);
    GroundDetailSample mud = SampleGroundDetailLayer(_TrenchSoilLayers.z, positionWS.xz, dpdx, dpdy);

    // цвета (линейные): лёссовидный суглинок, чернозём комьями в нём, грязь дна
    const half3 loamTone = half3(0.30, 0.22, 0.13);
    const half3 humusTone = half3(0.055, 0.04, 0.028);
    const half3 mudTone = half3(0.085, 0.06, 0.038);
    // выброс — смесь лёсса и чернозёма (окоп прорезает метр чернозёма над лёссом): пятнами по метру с лишним где темнее,
    // где светлее; комья мелкие и мягкие, вдали гаснут (пиксель шире 12 см) — крупные контрастные читались пятнами-мазками
    half mixed = PuddleNoise(positionWS.xz / 1.3 + 41.9) * 0.6 + PuddleNoise(positionWS.xz / 0.45 + 7.3) * 0.4;
    half3 spoilTone = lerp(loamTone, humusTone, 0.18 + 0.32 * mixed);
    float footprint = max(length(dpdx), length(dpdy));
    half clods = smoothstep(0.6, 0.85, PuddleNoise(positionWS.xz / 0.22 + 23.7)) * 0.5 * saturate(1.5 - footprint / 0.08);
    half3 spoil = lerp(spoilTone * loam.ratio, humusTone * lumps.ratio, clods);
    half3 own = lerp(spoil, mudTone * mud.ratio, bottom);
    albedo = lerp(albedo, own, bare);
    smoothness = lerp(smoothness, lerp(0.08, 0.26, bottom), bare);
    // на дне неба мало: стенки закрывают его (окоп глубже своей ширины)
    occlusion *= lerp(1, lerp(loam.ao, mud.ao * 0.55, bottom), bare);

    // рельеф почвы — в плоскости XZ мира, как у фактуры земли
    half3 normalTS = normalize(lerp(loam.normalTS, mud.normalTS, bottom));
    normalTS.xy *= bare;
    float3 normalWS = inputData.normalWS;
    float3 tangentWS = normalize(cross(normalWS, float3(0, 0, 1)));
    float3 bitangentWS = cross(tangentWS, normalWS);
    inputData.normalWS = normalize(tangentWS * normalTS.x + bitangentWS * normalTS.y + normalWS * normalTS.z);
}

#endif
