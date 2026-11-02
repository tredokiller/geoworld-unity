// Мокрая земля, лужи в дождь и вода болот — прямо в шейдере террейна (Game/Terrain Lit (Wet), форк URP Terrain Lit):
// вода непрозрачная, поэтому туман Enviro ложится на неё сам, трава стоит в ней правильно (и оседает в неё —
// PuddleSink.hlsl с тем же решением), лишних отрисовок нет, и она есть на всех загруженных тайлах на любой дистанции.
// Силу дождя и время считает GroundWetness:
//  - _GroundWetness (0..1): земля темнеет и блестит — грунт и дорога сильнее травы (голость — по цвету слоя/спутника);
//  - _PuddleLevel (0..1): лужи набираются. Где им быть — пригодность земли (Puddles.hlsl: карта земель GeoWorld и уклон
//    в окне вокруг камеры — больше всего на грунтовках и тропинках, меньше на пашне и асфальте, мало на лугу и в лесу),
//    голость по цвету и ровность; форма — шум трёх масштабов, поэтому лужи растут из одних и тех же мест и сливаются;
//  - в луже вода: тёмное мутное дно, гладь-зеркало (небо Enviro через отражения URP), кольца от капель, рябь от потока
//    винтов (поле RotorWashWater, вокруг дрона гладь тускнеет), а сильный поток выдувает воду из лужи под дроном;
//  - болото (карта земель — Wetland): стоячая вода между кочек и в сухую погоду — бурая, торфяная, с ряской, в дождь её
//    больше. Лужа — тонкая вода на грунте; болото — свой вид воды;
//  - поля (FieldRows.hlsl): рядки, пары колей техники, полосы комбайна, борозды пашни по направлению поля OSM;
//  - пора года (Seasons.hlsl): летом зелёный посев на пашне — убранное поле (стерня); зелень снимка осенью желтеет,
//    весной сочнее, зимой бурая, кроны леса на снимке осенью — в цветах осенних деревьев; под деревьями — лесная
//    подстилка по поре (ForestFloor.hlsl); зимой снег (Snow.hlsl: текстура,
//    надувы, земля сквозь снег, на грунтовках накатанный) по покрову _SnowCover, вода луж и болот подо льдом — тёмным,
//    гладким, с белыми трещинами; поверх льда снег полосами по ветру.
#ifndef GAME_GROUND_WETNESS_INCLUDED
#define GAME_GROUND_WETNESS_INCLUDED

#include "Puddles.hlsl"
#include "FieldRows.hlsl"
#include "../FieldWind.hlsl"
#include "../Water/WaterInteraction.hlsl"
#include "../Seasons.hlsl"
#include "../Snow.hlsl"
#include "GroundDetail.hlsl"
#include "ForestFloor.hlsl"

float _GroundWetness;
// покрытие дорог по карте дорог (RoadMap): после поры года, до снега
#include "GroundRoads.hlsl"
// Вода тайлов у берега (WaterShore): R — высота ближней глади (м, мир), G — до неё по горизонтали (м, далеко — 1000);
// окно вокруг камеры (_WaterShoreRect: xy — начало в мире, z — 1/размер, w — есть ли). Сэмплер — общий sampler_LinearClamp:
// у прохода их уже 16
TEXTURE2D(_WaterShore);
float4 _WaterShoreRect;

// Мокрый берег (0..1): полоса на ~0.15–0.45 м над урезом (край рваный, по шуму), не дальше ~5 м от воды по горизонтали
// (на пологом берегу выше — уже сухо); у края окна сходит на нет. Ниже глади (дно у берега) — тоже мокро. С блеском до
// 0.5 полоса под солнцем и небом светлела в песок — мокрая земля здесь в основном темнее.
half WaterShoreWet(float3 positionWS)
{
    half wet = 0;

    [branch] if (_WaterShoreRect.w > 0.5)
    {
        float2 uv = (positionWS.xz - _WaterShoreRect.xy) * _WaterShoreRect.z;
        float2 edge = min(uv, 1 - uv);
        half inside = saturate(min(edge.x, edge.y) * 12);

        if (inside > 0)
        {
            half2 shore = SAMPLE_TEXTURE2D_LOD(_WaterShore, sampler_LinearClamp, uv, 0).rg;
            float above = positionWS.y - shore.r;
            float band = 0.15 + 0.3 * PuddleNoise(positionWS.xz / 3.1 + 17.3);
            wet = (1 - smoothstep(band * 0.3, band, above)) * (1 - smoothstep(2.5, 5, shore.g)) * inside;
        }
    }

    return wet;
}

// живое небо Enviro с облаками (WaterSky): вода луж и болот отражает его, как вода рек и озёр (WaterCore.hlsl), а не
// запечённое голубое небо пробы — в дождь под серым небом лужи были голубыми. Сэмплер — общий sampler_LinearClamp (у
// варианта Forward+ сэмплера пробы unity_SpecCube0 нет)
TEXTURECUBE(_WaterSkyCube);
float _WaterSkyOn;

// После света URP: в воде луж и болот (water — её доля) небо пробы меняется на живое — с тем же весом окружения BRDF.
half3 PuddleSky(half3 color, InputData inputData, half3 albedo, half smoothness, half occlusion, half water)
{
    [branch] if (_WaterSkyOn > 0.5 && water > 0.001)
    {
        BRDFData brdf;
        half alpha = 1;
        InitializeBRDFData(albedo, 0, half3(0, 0, 0), smoothness, alpha, brdf);
        float3 reflected = reflect(-inputData.viewDirectionWS, inputData.normalWS);
        half3 weight = EnvironmentBRDFSpecular(brdf, Pow4(1 - saturate(dot(inputData.normalWS, inputData.viewDirectionWS)))) * occlusion;
        half3 probe = GlossyEnvironmentReflection(reflected, inputData.positionWS, brdf.perceptualRoughness, 1, inputData.normalizedScreenSpaceUV);
        half3 sky = SAMPLE_TEXTURECUBE_LOD(_WaterSkyCube, sampler_LinearClamp, reflected, PerceptualRoughnessToMipmapLevel(brdf.perceptualRoughness)).rgb;
        color += (sky - probe) * weight * water;
    }

    return color;
}

// Лёд на луже или болотной воде (зимой): чистый — тёмный, сквозь него чёрная вода; пятнами белый (смёрзшийся снег);
// белые трещины. Снег поверх — полосами по ветру зимы: при полном покрове на льду его ~55 %, ветер выметает лёд пятнами.
half3 GroundIce(float2 p, float footprint, out half smoothness, out half snowShare)
{
    half white = smoothstep(0.1, 0.35, SnowNoise(p / 9.0, 221u) + SnowNoise(p / 2.7, 222u) * 0.4);
    half3 ice = lerp(half3(0.045, 0.06, 0.07), half3(0.36, 0.41, 0.44), white);
    float crackWidth = max(0.025, footprint * 0.5);
    half crack = (1 - smoothstep(0, crackWidth, abs(SnowNoise(p / 1.9, 223u)))) * saturate(0.025 / crackWidth * 2);
    ice = lerp(ice, half3(0.6, 0.65, 0.68), crack * (1 - white * 0.6));
    smoothness = lerp(lerp(0.9, 0.6, white), 0.5, crack);
    float2 across = float2(-SNOW_WIND.y, SNOW_WIND.x);
    float streak = 0.5 + SnowNoise(float2(dot(p, SNOW_WIND) / 17.0, dot(p, across) / 4.0), 224u) * 0.8;
    snowShare = smoothstep(0.42, 0.5, streak);
    return ice;
}

// water — доля воды луж и болот в пикселе (для живого неба в отражении, PuddleSky)
void GroundWetness(inout InputData inputData, half3 geometryNormalWS, inout half3 albedo, inout half smoothness, inout half occlusion, out half water)
{
    water = 0;
    float3 positionWS = inputData.positionWS;
    half2 land = PuddleLand(positionWS);
    float distanceToCamera = distance(positionWS, _WorldSpaceCameraPos);
    // производные — до ветвлений: по ним мипы текстуры снега и ширина краёв снега и трещин
    float2 dpdx = ddx(positionWS.xz);
    float2 dpdy = ddy(positionWS.xz);
    float footprint = max(length(dpdx), length(dpdy));

    // голая земля — по цвету самой земли, до поры года и снега: осенью пожелтевший луг и зимой солома и снег читались
    // «голыми» — лужи разливались по лугу, а трава вокруг них не оседала (PuddleSink решает по карте, без поры)
    half groundGreenness = (albedo.g - max(albedo.r, albedo.b)) / max(albedo.g, 0.02);
    half bare = saturate(1 - groundGreenness * 4);

    // поля: рядки, колеи техники, борозды по направлению поля (FieldRows.hlsl) — до поры года и снега
    FieldRows(positionWS, dpdx, dpdy, groundGreenness, albedo, inputData.normalWS);

    // пора года: летом убранные поля, зелень по поре; зимой снег — на грунтовках и тропинках накатанный, серее
    half fieldShare = GroundFieldShare(positionWS);
    albedo = SeasonGround(SeasonForestCanopy(SeasonHarvest(albedo, fieldShare), positionWS.xz));
    // волны ветра по зелёному лугу и молодым посевам — за дальностью травы ими живёт земля (летом поле убрано — стерня не
    // волнуется); под снегом нет
    [branch] if (_SnowCover < 0.001)
    {
        half grassy = smoothstep(0.04, 0.2, groundGreenness) * (1.0 - fieldShare * _Season.y);
        albedo = FieldWindSheen(albedo, positionWS.xz, grassy * 0.9);
    }

    // под деревьями — лесная подстилка по поре года, а не цвет крон со снимка (ForestFloor.hlsl); мокнет она как голая земля.
    // Снег решает по земле до неё: тёмная мокрая подстилка — «тёмная земля», на которой снег тает первым, и зимой лес
    // оставался почти без снега, хотя в его тени снег лежит дольше
    half3 floorless = albedo;
    half litter;
    ForestFloor(inputData, dpdx, dpdy, distanceToCamera, albedo, smoothness, occlusion, litter);
    bare = max(bare, litter);

    // полотно дорог: асфальт, гравий, грунтовка с колеями, тропинки (GroundRoads.hlsl); roadHollow — где на нём стоит вода
    half roadSurface;
    half roadHollow;
    GroundRoad(inputData, dpdx, dpdy, albedo, smoothness, occlusion, roadSurface, roadHollow);

    half snow = 0;
    half3 snowAlbedo = SEASON_SNOW_COLOR;
    half snowSmoothness = 0.25;
    float3 snowNormal = geometryNormalWS;

    [branch] if (_SnowCover > 0.001)
    {
        // на дорогах снег укатан и серее: по карте дорог, где она есть, иначе по карте земель
        half packed = max(smoothstep(0.85, 0.95, land.r), roadSurface);
        // земля для снега — без подстилки (на дорогах — с полотном)
        half3 snowGround = lerp(albedo, floorless, litter * (1 - roadSurface));
        SnowSurface surface = GetSnowSurface(positionWS, geometryNormalWS, snowGround, packed, distanceToCamera, dpdx, dpdy);
        snow = SnowCoverAt(positionWS, geometryNormalWS, snowGround, packed, surface.height, distanceToCamera, footprint);
        snowAlbedo = surface.albedo;
        snowSmoothness = surface.smoothness;
        snowNormal = surface.normalWS;
        albedo = lerp(albedo, snowAlbedo, snow);
        smoothness = lerp(smoothness, snowSmoothness, snow);
        occlusion = lerp(occlusion, 1, snow);
        inputData.normalWS = normalize(lerp(inputData.normalWS, snowNormal, snow));
    }

    // берег у воды мокрый и в сухую погоду: полоса над урезом темнее и блестит (грунт сильнее травы); под снегом нет
    half shore = WaterShoreWet(positionWS) * (1 - snow);
    albedo *= lerp(1, lerp(0.7, 0.5, bare), shore);
    smoothness = max(smoothness, shore * lerp(0.12, 0.28, bare));

    // сухо и не болото — ничего не считаем: ветвление по глобальным и по карте, на большей части кадра одно
    if (_GroundWetness <= 0.001 && _PuddleLevel <= 0.001 && land.g <= 0.004)
        return;

    // болото сырое всегда; снег от сырости не темнеет
    half wet = max(_GroundWetness, land.g * 0.8) * (1 - snow);
    albedo *= lerp(1, lerp(0.8, 0.6, bare), wet);
    smoothness = max(smoothness, wet * lerp(0.2, 0.42, bare));

    if (_PuddleLevel <= 0.001 && land.g <= 0.004)
        return;

    // лужи на дорогах: в колеях грунтовок и гравийки, у асфальта вдоль кромки и в выбоинах (GroundRoads.hlsl) — тем больше,
    // чем дольше дождь, с каймой мокрой грязи; на крутом склоне вода стекает
    half roadPuddle = 0;
    half roadRim = 0;

    [branch] if (roadHollow > 0.01 && _PuddleLevel > 0.001)
    {
        float roadThreshold = lerp(1.0, 0.4, _PuddleLevel);
        float roadField = roadHollow * (0.7 + 0.3 * PuddleNoise(positionWS.xz / 1.3 + 61.3)) * smoothstep(0.95, 0.985, geometryNormalWS.y);
        roadPuddle = smoothstep(roadThreshold - 0.05, roadThreshold + 0.05, roadField) * (1 - snow);
        roadRim = smoothstep(roadThreshold - 0.2, roadThreshold - 0.02, roadField) * (1 - roadPuddle) * (1 - snow);
    }

    // ровность — по нормали самого рельефа: с картами нормалей слоёв у грунта и гравия точки взлёта лужи дробились
    // в крапинки
    half flat = smoothstep(0.990, 0.998, geometryNormalWS.y);
    // где лужам быть, решает карта (на воде, зданиях и рельсах — нигде), цвет только поправляет: голая земля держит
    // воду лучше, трава хуже. Болоту порог свой — вода там стоит и без дождя
    float puddleThreshold = _PuddleLevel > 0.001 ? PuddleThreshold(land.r * lerp(0.8, 1.1, bare)) : 2;
    float marshThreshold = MarshThreshold(land.g);
    // рядом трава стоит над водой и закрывает её кромку (в самой воде оседает — видно середину), а дальше ~60 м траву
    // не рисуют: там на травянистой земле и в тростнике болота воды показываем меньше, тем меньше, чем более косо на неё
    // смотрим (вблизи земли трава закрыла бы её всю), — и она не разливается за кругом травы
    half view = saturate((_WorldSpaceCameraPos.y - positionWS.y) / max(distanceToCamera, 1));
    float grassCover = max(1 - bare, land.g) * saturate((distanceToCamera - 40) / 60) * (1 - view);
    float threshold = min(puddleThreshold, marshThreshold) + grassCover * 0.1;
    half allowed = flat * step(0.02, max(land.r, land.g));

    // шум до такого не дорастает и на дороге воды нет — воды здесь нет (шум и всё дальше не считаем)
    if ((allowed <= 0 || threshold > 0.97) && roadPuddle <= 0.001 && roadRim <= 0.001)
        return;

    float field = PuddleField(positionWS.xz, distanceToCamera);
    // край шире там, где пиксель накрывает много шума (вдали): вода не искрит точками
    float edge = max(fwidth(field), 0.03);
    half puddle = smoothstep(threshold - edge * 0.5, threshold + edge, field) * allowed;
    // кромка — мокрая грязь вокруг воды
    half rim = smoothstep(threshold - 0.06 - edge, threshold, field) * allowed * (1 - puddle);
    // вода болотная, где её держит болото, а не дождь
    half swamp = saturate((puddleThreshold - marshThreshold) * 12 + 0.5) * step(0.004, land.g);
    // и лужи дорог — дождевые
    puddle = max(puddle, roadPuddle);
    rim = max(rim, roadRim) * (1 - puddle);
    swamp *= 1 - roadPuddle;

    albedo *= lerp(1, 0.75, rim * (1 - snow));
    smoothness = max(smoothness, rim * 0.55 * (1 - snow));

    if (puddle <= 0.001)
        return;

    // зимой вода луж и болот подо льдом (GroundIce): без ряби, колец и ряски, поток его не выдувает
    half frozen = _Season.w;
    half iceSmoothness = 0.9;
    half iceSnow = 0;
    half3 ice = 0;

    [branch] if (frozen > 0.5)
        ice = GroundIce(positionWS.xz, footprint, iceSmoothness, iceSnow);

    // поток винтов рябит воду (гладь мутнеет и тускнеет, кольца капель тонут), а сильный и низкий выдувает воду из лужи
    // под дроном — открывается тёмная, насквозь мокрая грязь (болотную — нет: её там по колено)
    half washRough = WaterWashRough(WaterWashAir(positionWS)) * (1 - frozen);
    half blown = puddle * WaterWashBlow(positionWS) * (1 - swamp) * (1 - frozen);
    puddle -= blown;
    albedo *= 1 - blown * 0.45;
    smoothness = max(smoothness, blown * 0.6);

    // плёнка и мелкая ряска на болотной воде — редкие тусклые пятна (~8 % воды); поток винтов их разгоняет (ряска пятнами —
    // у воды болот, Game/Water/Swamp)
    half weed = smoothstep(0.66, 0.76, PuddleNoise(positionWS.xz / 0.9 + 53.3) * 0.6 + PuddleNoise(positionWS.xz / 0.25 + 71.9) * 0.4);
    weed *= swamp * puddle * (1 - WaterWashClear(positionWS)) * (1 - frozen);

    half foam;
    float2 slope = WaterRippleSlope(positionWS, foam) * lerp(1.2, 0.8, swamp) +
                   WaterRainSlope(positionWS, distanceToCamera, _WaterRain) * 1.4 * (1 - 0.7 * washRough);
    slope *= (1 - weed) * (1 - frozen);
    foam *= 1 - frozen;
    float3 normalWS = normalize(lerp(inputData.normalWS, float3(0, 1, 0), puddle * (1 - weed * 0.5)));
    inputData.normalWS = normalize(normalWS + float3(-slope.x, 0, -slope.y) * puddle);

    // лужа: мутная вода темнит дно, гладь как зеркало; болото: бурая торфяная вода, дна не видно; зимой — лёд
    half3 waterColour = lerp(lerp(albedo * 0.3 + 0.015, half3(0.035, 0.03, 0.016), swamp), ice, frozen);
    albedo = lerp(albedo, waterColour, puddle);
    // пена кружевом, как на воде (WaterWashLace), по своему шуму — не сплошным пятном
    albedo = lerp(albedo, 0.55, WaterWashLace(foam, PuddleNoise(positionWS.xz / 0.35 + 91.7)) * puddle * 0.5);
    albedo = lerp(albedo, half3(0.03, 0.04, 0.012), weed);
    // вдали чуть мягче — не искрит; в потоке винтов рябь размывает отражение
    half mirror = WaterWashSmoothness(lerp(0.96, 0.85, saturate(distanceToCamera / 300)), washRough);
    smoothness = lerp(smoothness, lerp(lerp(mirror, 0.45, weed), iceSmoothness, frozen), puddle);
    occlusion = lerp(occlusion, 1, puddle);
    water = puddle * (1 - weed);

    // снег поверх льда — полосами по ветру: на льду его меньше, чем на земле
    half snowOnIce = snow * puddle * frozen * iceSnow;
    albedo = lerp(albedo, snowAlbedo, snowOnIce);
    smoothness = lerp(smoothness, snowSmoothness, snowOnIce);
    inputData.normalWS = normalize(lerp(inputData.normalWS, snowNormal, snowOnIce));
}

#endif
