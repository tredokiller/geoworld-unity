// Общее для воды GeoWorld. Вид у каждой воды свой — свой шейдер со своей поверхностью, цветом и поведением:
//  - Game/Water/River — река: течёт вниз по руслу (поток в UV3 меша, его считает WaterSurfaces по осям рек), мутная,
//    у берегов на быстрине пена;
//  - Game/Water/Lake — пруд и озеро: стоячая, в штиль — зеркало, рябь по ветру игры пятнами порывов, ряска у берегов;
//  - Game/Water/Swamp — вода болот: бурая торфяная, почти без волны (тростник и ряска гасят ветер), ряски много;
//  - Game/Water/Sea — открытая вода (водохранилище, море): волны от ветра трёх масштабов, барашки, прибой у берега;
//  - лужи и вода между кочек болот — в шейдере террейна (GroundWetness.hlsl): тонкая вода на земле.
// Здесь — то, что у них одно: вершины, дно сквозь воду (глубина и копия кадра), отражение берега и деревьев из копии кадра,
// ветер игры, полосы от винтов, свет URP, туман и облака Enviro. Шейдер воды подключает до этого файла Core, Lighting,
// DeclareDepthTexture, DeclareOpaqueTexture и туман Enviro (#include_with_pragmas), а свой CBUFFER объявляет сам.
#ifndef GAME_WATER_CORE_INCLUDED
#define GAME_WATER_CORE_INCLUDED

#include "WaterInteraction.hlsl"
#include "../Seasons.hlsl"
#include "../Snow.hlsl"

// 1 — у камеры есть копия кадра (преломление), 0 — нет (WaterRefraction ставит перед каждой камерой)
float _WaterRefraction;
// живое небо Enviro с облаками (WaterSky); _WaterSkyOn 0 — его нет (окно Scene, первые кадры), тогда небо пробы, как было.
// Сэмплер — пробы URP (samplerunity_SpecCube0): свой был бы лишним из 16 на проход
TEXTURECUBE(_WaterSkyCube);
float _WaterSkyOn;
#ifndef GAME_WATER_WIND_GLOBALS
#define GAME_WATER_WIND_GLOBALS
// ветер игры (VegetationWind, ветер текущего дрона): xy — куда дует (мир xz), z — скорость, м/с (ещё FieldWind.hlsl)
float4 _WaterWind;
// пробег ветра (м, мир xz): копится по кадрам — рябь бежит по ветру и не прыгает, когда ветер меняется
float4 _WaterWindRun;
#endif

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    // у реки (WaterSurfaces): метры от её оси со знаком и полуширина, м; у озёр и морей — ноль
    float2 bank : TEXCOORD2;
    // поток реки (м/с, мир xz); у озёр и морей — ноль
    float2 flow : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    float4 screenPos : TEXCOORD2;
    float2 flow : TEXCOORD3;
    float2 bank : TEXCOORD4;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings WaterVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
    output.positionCS = position.positionCS;
    output.positionWS = position.positionWS;
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.screenPos = ComputeScreenPos(position.positionCS);
    output.flow = input.flow;
    output.bank = input.bank;
    return output;
}

// Нормаль карты, уложенной по миру (x карты — на восток, y — на север), в касательных осях воды: x — X мира,
// y — Z мира, z — вверх. Сила — как NormalStrength у Shader Graph.
half3 WaterNormalOf(half4 packed, half strength)
{
    half3 n = UnpackNormal(packed);
    return half3(n.xy * strength, lerp(1, n.z, saturate(strength)));
}

// Сложение нормалей, как NormalBlend у Shader Graph.
half3 WaterBlend(half3 a, half3 b)
{
    return normalize(half3(a.xy + b.xy, a.z * b.z));
}

// Ветер игры: куда дует и скорость (м/с).
float2 WaterWindDirection()
{
    float2 direction = _WaterWind.xy;
    return dot(direction, direction) > 1e-4 ? normalize(direction) : float2(1, 0);
}

half WaterWindSpeed()
{
    return _WaterWind.z;
}

// Сдвиг карты с шагом tiling метров, которую гонит ветер со скоростью rate от его собственной (доля тайла, 0..1).
float2 WaterWindScroll(float tiling, float rate)
{
    return frac(_WaterWindRun.xy * (rate / tiling));
}

// Плавный шум 0..1 по миру (целочисленный хеш: на координатах в километрах float-хеш терял точность).
float WaterValueNoise(float2 p, uint salt)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3 - 2 * f);
    float a = WaterHash(i, salt), b = WaterHash(i + float2(1, 0), salt);
    float c = WaterHash(i + float2(0, 1), salt), d = WaterHash(i + 1, salt);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// Рябь мельче пикселя с расстояния не исчезает: её наклоны размывают небо в воде. Карту нормалей вдали гладят мипы и
// _FarNormal (иначе мельтешит), и вода с высоты становилась зеркальнее, чем вблизи. Потерянный разброс наклонов
// возвращаем шероховатостью: когда пиксель (footprint, м) крупнее шестнадцатой доли тайла карты (tile, м), шероховатость
// растёт и к пикселю в треть тайла доходит до разброса наклонов ряби (slope ~ сила карты).
half WaterFarSmoothness(half smoothness, float footprint, float tile, half slope)
{
    half t = saturate(log2(max(footprint * 16 / max(tile, 0.01), 1)) / 3);
    half roughness = 1 - smoothness;
    return 1 - sqrt(roughness * roughness + slope * slope * t);
}

// Пиксель воды: точка, взгляд, экран.
struct WaterView
{
    float3 positionWS;
    float3 view;
    float distance;
    float2 screenUV;
    float surfaceDepth;
    // разница глубин камеры — вдоль её оси; вдоль луча к краю кадра путь длиннее (до ×1.5)
    float rayScale;
    // производные мировых xz по экрану — взяты до ветвлений (лёд зимой: мипы снега, ширина трещин и краёв)
    float2 dpdx;
    float2 dpdy;
};

WaterView GetWaterView(Varyings input)
{
    WaterView v;
    v.positionWS = input.positionWS;
    float3 toCamera = GetWorldSpaceViewDir(input.positionWS);
    v.distance = length(toCamera);
    v.view = toCamera / max(v.distance, 1e-4);
    v.screenUV = input.screenPos.xy / input.screenPos.w;
    v.surfaceDepth = input.screenPos.w;
    v.rayScale = v.distance / max(v.surfaceDepth, 1e-4);
    v.dpdx = ddx(input.positionWS.xz);
    v.dpdy = ddy(input.positionWS.xz);
    return v;
}

// Что под водой: глубина воды вдоль луча (м) в пикселе и в точке, откуда пришёл преломлённый свет, и цвет дна оттуда.
struct WaterBelow
{
    float depthHere;
    float depthBelow;
    half3 colour;
};

WaterBelow SampleBelow(WaterView v, half2 normalXY, half distortion)
{
    WaterBelow below;
    float sceneDepth = LinearEyeDepth(SampleSceneDepth(v.screenUV), _ZBufferParams);
    below.depthHere = max(0, sceneDepth - v.surfaceDepth) * v.rayScale;
    below.depthBelow = below.depthHere;
    below.colour = 0;

    if (_WaterRefraction > 0.5)
    {
        float2 bend = normalXY * (distortion * 0.3) * float2(1, _ScreenParams.x / _ScreenParams.y);
        float2 refractUV = v.screenUV + bend / max(v.surfaceDepth, 1);
        float bentDepth = LinearEyeDepth(SampleSceneDepth(refractUV), _ZBufferParams);

        // то, что над водой, преломляться не должно: там берём прямой луч
        if (bentDepth < v.surfaceDepth)
        {
            refractUV = v.screenUV;
            bentDepth = sceneDepth;
        }

        below.depthBelow = max(0, bentDepth - v.surfaceDepth) * v.rayScale;
        below.colour = SampleSceneColor(refractUV);
    }

    return below;
}

// Толща и дно: дно сквозь воду тонировано её цветом, глубже — сама вода (opacity), у кромки — чистая вода (clean). Дно
// в копии кадра уже освещено и в тенях: прибавляем его как есть (transmitted), освещением считаем только толщу (albedo).
void WaterBody(WaterBelow below, half3 colour, half opacity, half clean, out half3 albedo, out half3 transmitted)
{
    albedo = colour;
    transmitted = 0;

    if (_WaterRefraction > 0.5)
    {
        half body = opacity * clean;
        transmitted = below.colour * lerp(1, saturate(colour * 3), clean) * (1 - body);
        albedo = colour * body;
    }
}

// Струя от винтов растекается по воде полосами от дрона: карта нормалей в полярных осях вокруг точки под дроном — от края
// лунки и докуда поток рябит воду (WaterWashAir, порог — как у ряби); белые — где воздух у воды быстрее ~5 м/с. Полосы
// крупные (12 по кругу, ~7 м вдоль, бегут от дрона ~6 м/с) и без мелочи карты (мип на две ступени грубее): мелкая рябь
// карты поверх бликов неба читалась серым асфальтом.
void WaterWashStreaks(float2 p, float t, TEXTURE2D_PARAM(normalMap, normalSampler), half amount, inout half3 normalTS, inout half washFoam)
{
    if (_WaterWashPower <= 0.001 || amount <= 0.001)
        return;

    float2 offset = p - _WaterWashPosition.xz;
    float r = length(offset);
    float radius = max(_WaterWashPosition.w, 0.1);
    half air = WaterWashAir(float3(p.x, 0, p.y));
    half ring = smoothstep(0.012, 0.09, air) * smoothstep(radius * 0.3, radius, r);
    float angle = atan2(offset.y, offset.x) * (12.0 / 6.2831853);
    float2 polar = float2(angle, r * 0.15 - t * 0.9);
    // по кругу координата рвётся (±π, скачок на 12): производные без скачка, мип — по ним
    float angleDx = ddx(angle), angleDy = ddy(angle);
    angleDx -= 12 * round(angleDx / 12);
    angleDy -= 12 * round(angleDy / 12);
    half3 streak = WaterNormalOf(SAMPLE_TEXTURE2D_GRAD(normalMap, normalSampler, polar,
        float2(angleDx, ddx(polar.y)) * 4, float2(angleDy, ddy(polar.y)) * 4), 0.6 * ring * amount);
    // карта лежит в полярных осях: x — по кругу, y — от дрона; разворачиваем в оси мира
    float2 radial = offset / max(r, 1e-4);
    float2 tangential = float2(-radial.y, radial.x);
    streak.xy = streak.x * tangential + streak.y * radial;
    normalTS = WaterBlend(normalTS, streak);
    washFoam = max(washFoam, saturate(air * 4 - 1) * ring * 0.4 * amount);
}

// Рябь под дроном и кольца от капель — наклоны поверхности (dh/dx, dh/dz) поверх нормали.
half3 WaterAddSlope(half3 normalTS, float2 slope)
{
    return normalize(half3(normalTS.xy - slope * normalTS.z, normalTS.z));
}

// Небо в отражении воды по направлению и шероховатости: живое (WaterSky), без него — проба URP.
half3 WaterSkyReflection(float3 reflected, float3 positionWS, half perceptualRoughness, float2 screenUV)
{
    half3 sky;

    [branch] if (_WaterSkyOn > 0.5)
        sky = SAMPLE_TEXTURECUBE_LOD(_WaterSkyCube, samplerunity_SpecCube0, reflected, PerceptualRoughnessToMipmapLevel(perceptualRoughness)).rgb;
    else
        sky = GlossyEnvironmentReflection(reflected, positionWS, perceptualRoughness, 1, screenUV);

    return sky;
}

// Отражение неба и того, что над водой, поверх света URP. UniversalFragmentPBR уже положил небо пробы (Default-Skybox:
// чистое голубое, без облаков) с весом окружения BRDF — меняем его на живое небо, а там, где луч по копии кадра упёрся в
// берег или деревья, — на них (mirror.a — насколько лучу верить, sharp — насколько вода гладкая для чёткого отражения).
half3 WaterReflections(half3 lit, SurfaceData surface, float3 positionWS, float3 view, float3 normalWS, float2 screenUV, half4 mirror, half sharp)
{
    BRDFData brdf;
    InitializeBRDFData(surface, brdf);
    float3 reflected = reflect(-view, normalWS);
    half3 weight = EnvironmentBRDFSpecular(brdf, Pow4(1 - saturate(dot(normalWS, view)))) * surface.occlusion;
    half3 probe = GlossyEnvironmentReflection(reflected, positionWS, brdf.perceptualRoughness, 1, screenUV);
    half3 sky = WaterSkyReflection(reflected, positionWS, brdf.perceptualRoughness, screenUV);
    return lit + (lerp(sky, mirror.rgb, mirror.a * sharp) - probe) * weight;
}

// Отражение того, что над водой (берег, деревья, тростник), — из копии кадра по буферу глубины: луч от воды по отражённому
// взгляду идёт шагами (от 0.15 м + 2 % дальности до воды, каждый следующий в 1.3 раза длиннее — на сотни метров) до
// первой поверхности, за которую ушёл, и уточняется делением пополам. a — насколько ему верить: у краёв кадра и на длинных
// лучах меньше; где луч ушёл за кадр или в небо — 0, там отражение пробы (небо Enviro). Без копии кадра — 0.
half4 WaterScreenReflection(float3 positionWS, float3 reflected, float distanceToCamera)
{
    if (_WaterRefraction < 0.5 || reflected.y < 0.01 || distanceToCamera > 800)
        return 0;

    float stepLength = 0.15 + distanceToCamera * 0.02;
    float3 from = positionWS;
    float3 to = positionWS;

    [loop]
    for (int i = 0; i < 24; i++)
    {
        from = to;
        to += reflected * stepLength;
        stepLength *= 1.3;
        float4 clip = TransformWorldToHClip(to);

        if (clip.w <= 0)
            return 0;

        float4 screen = ComputeScreenPos(clip);
        float2 uv = screen.xy / screen.w;

        if (any(uv <= 0) || any(uv >= 1))
            return 0;

        float behind = clip.w - LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);

        // ещё перед поверхностью — или ушёл за тонкое (ствол, стебель) далеко, это не она: идём дальше
        if (behind <= 0 || behind > stepLength * 2 + 0.5)
            continue;

        [unroll]
        for (int j = 0; j < 4; j++)
        {
            float3 middle = (from + to) * 0.5;
            float4 middleClip = TransformWorldToHClip(middle);
            float4 middleScreen = ComputeScreenPos(middleClip);

            if (middleClip.w - LinearEyeDepth(SampleSceneDepth(middleScreen.xy / middleScreen.w), _ZBufferParams) > 0)
                to = middle;
            else
                from = middle;
        }

        float4 hit = ComputeScreenPos(TransformWorldToHClip(to));
        float2 hitUV = hit.xy / hit.w;
        float2 edge = min(hitUV, 1 - hitUV);
        half trust = saturate(min(edge.x, edge.y) * 12) * saturate(2 - i / 12.0);
        return half4(SampleSceneColor(hitUV), trust);
    }

    return 0;
}

// Ячейки Вороного для трещин льда: до ближайшей грани (F2 − F1, в долях ячейки).
float IceCells(float2 p, uint salt)
{
    float2 i = floor(p);
    float2 f = p - i;
    float d1 = 8;
    float d2 = 8;

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 g = float2(x, y);
            float2 r = g + float2(WaterHash(i + g, salt), WaterHash(i + g, salt + 1u)) - f;
            float d = dot(r, r);
            d2 = d < d1 ? d1 : min(d2, d);
            d1 = min(d1, d);
        }
    }

    return sqrt(d2) - sqrt(d1);
}

// Линия трещины по расстоянию до грани ячеек: ширина width (в долях ячейки); где пиксель шире её, линия шире пикселя не
// становится — бледнеет (вдали тонкая серая нить, а не мельтешение). pixel — сколько долей ячейки в пикселе.
half IceCrackLine(float edge, float width, float pixel)
{
    float w = max(width, pixel * 1.5);
    return (1 - smoothstep(0, w, edge)) * saturate(width / w * 1.5);
}

// Лёд зимой на стоячей воде (озеро, болото, море; река течёт и остаётся открытой). Как он выглядит с дрона:
//  - чистый лёд тёмный (сквозь него чёрная вода, у мели — дно) и гладкий: в нём отражаются небо, берег и деревья (из копии
//    кадра, как у воды);
//  - белый лёд (смёрзшийся снег, шуга) пятнами в десятки метров — светло-серый, матовый;
//  - трещины — белые нити: крупная сеть (~14 м) видна с высоты, мелкая (~3.5 м) — вблизи, часть граней не треснула; вокруг
//    трещины в толще — светлый ореол;
//  - пузырьки воздуха в толще — белые точки на двух глубинах: при движении камеры сдвигаются, видна толща льда;
//  - снег (Snow.hlsl) полосами по ветру зимы, у берега сугробы: при полном покрове на льду ~60 % снега (ветер выметает лёд
//    пятнами), при половинном ~25 %; у кромки снега — тонкая пороша.
// snowBias — снега больше обычного (болото: кочки и тростник держат снег) или меньше (море: ветер выметает). Непрозрачный,
// у кромки — мягкий край по глубине, как у воды. Свет URP, туман и облака Enviro.
half4 WaterIceShade(Varyings input, WaterView v, half snowBias)
{
    float2 p = v.positionWS.xz;
    float footprint = max(length(v.dpdx), length(v.dpdy));
    float depthHere = max(0, LinearEyeDepth(SampleSceneDepth(v.screenUV), _ZBufferParams) - v.surfaceDepth) * v.rayScale;
    half shore = saturate(1 - depthHere * saturate(v.view.y) / 1.2);
    float3 up = normalize(input.normalWS);

    // чистый и белый лёд
    half whiteIce = smoothstep(0.42, 0.68, 0.5 + SnowNoise(p / 47.0, 301u) * 0.7 + SnowNoise(p / 13.0, 302u) * 0.3);
    half3 blackIce = lerp(half3(0.035, 0.05, 0.06), half3(0.09, 0.09, 0.075), shore * 0.6);
    half3 ice = lerp(blackIce, half3(0.38, 0.43, 0.46), whiteIce);
    half smoothness = lerp(0.94, 0.62, whiteIce);

    // трещины по искажённой сети ячеек; часть граней цела
    float2 warp = float2(SnowNoise(p / 9.0, 303u), SnowNoise(p / 9.0 + 7.1, 304u)) * 2.2;
    half intact = smoothstep(0.38, 0.55, 0.5 + SnowNoise(p / 6.0, 306u));
    float edgeA = IceCells((p + warp) / 14.0, 305u);
    half crack = IceCrackLine(edgeA, 0.011, footprint / 14.0) * intact;
    half halo = (1 - smoothstep(0, 0.06, edgeA)) * intact * 0.25;
    half nearCracks = saturate(1 - v.distance / 70);

    [branch] if (nearCracks > 0)
    {
        float edgeB = IceCells((p + warp * 0.4) / 3.6, 307u);
        crack = max(crack, IceCrackLine(edgeB, 0.02, footprint / 3.6) * nearCracks * 0.7 * smoothstep(0.35, 0.6, 0.5 + SnowNoise(p / 2.2, 308u)));
    }

    crack *= 1 - whiteIce * 0.7;
    ice = lerp(ice, half3(0.6, 0.65, 0.68), saturate(crack * 0.85 + halo * (1 - whiteIce)));
    smoothness = lerp(smoothness, 0.55, crack);

    // пузырьки в толще: точки в кучках, на глубине 4 и 11 см (сдвиг по лучу сквозь лёд, n ≈ 1.31)
    half nearBubbles = saturate(1 - v.distance / 18) * (1 - whiteIce);

    [branch] if (nearBubbles > 0)
    {
        float2 parallax = -v.view.xz / max(v.view.y, 0.2) / 1.31;
        half bubbles = 0;

        [unroll]
        for (int k = 0; k < 2; k++)
        {
            float2 q = (p + parallax * (k == 0 ? 0.04 : 0.11)) * (k == 0 ? 9.0 : 6.0);
            float2 cell = floor(q);
            uint salt = 311u + (uint)k * 4u;
            half present = step(0.8, WaterHash(cell, salt)) * smoothstep(0.05, 0.25, SnowNoise(p / 2.7 + k * 5.3, salt + 1u));
            float2 centre = float2(WaterHash(cell, salt + 2u), WaterHash(cell, salt + 3u)) * 0.6 + 0.2;
            float radius = 0.06 + 0.1 * WaterHash(cell, salt + 1u);
            bubbles = max(bubbles, present * (1 - smoothstep(radius * 0.6, radius, length(q - cell - centre))) * (k == 0 ? 0.9 : 0.6));
        }

        ice = lerp(ice, half3(0.7, 0.74, 0.76), bubbles * nearBubbles);
    }

    // снег полосами по ветру зимы (~41 м вдоль, ~9 м поперёк), у берега сугробы
    float2 across = float2(-SNOW_WIND.y, SNOW_WIND.x);
    float along = dot(p, SNOW_WIND);
    float side = dot(p, across);
    float streak = 0.5 + SnowNoise(float2(along / 41.0, side / 9.0), 321u) * 0.65 + SnowNoise(float2(along / 13.0, side / 3.4), 322u) * 0.3 +
                   SnowNoise(p / 4.1, 323u) * 0.15 * saturate(1 - v.distance / 200);
    streak += (shore * 0.45 + snowBias) * _SnowCover;
    // снег на льду не сереет от тёмного льда под ним (это у земли проступают поля): «земля» под ним светлая
    SnowSurface snowSurface = GetSnowSurface(v.positionWS, up, half3(0.3, 0.3, 0.3), 0, v.distance, v.dpdx, v.dpdy);
    streak += (snowSurface.height - 0.5) * 0.12 * saturate(1 - v.distance / 60);
    half threshold = lerp(1.0, 0.4, _SnowCover);
    half edge = max(footprint * 0.08, 0.02);
    half snow = smoothstep(threshold - edge, threshold + edge, streak);
    half dusting = smoothstep(threshold - 0.2, threshold - edge, streak) * (1 - snow);

    ice = lerp(ice, lerp(ice, snowSurface.albedo, 0.45), dusting);
    smoothness = lerp(smoothness, 0.45, dusting * 0.7);
    half3 albedo = lerp(ice, snowSurface.albedo, snow);
    smoothness = lerp(smoothness, snowSurface.smoothness, snow);
    // лёд почти ровный — с лёгкой волной застывшей ряби; снег — своя нормаль
    float2 ripple = float2(SnowNoise(p / 2.3, 331u), SnowNoise(p / 2.3 + 3.3, 332u)) * 0.03;
    float3 normalWS = normalize(lerp(normalize(up + float3(ripple.x, 0, ripple.y)), snowSurface.normalWS, snow));

    InputData inputData = (InputData)0;
    inputData.positionWS = v.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = v.view;
    inputData.shadowCoord = TransformWorldToShadowCoord(v.positionWS);
    inputData.bakedGI = SampleSH(normalWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = half4(1, 1, 1, 1);

    SurfaceData surface = (SurfaceData)0;
    surface.albedo = albedo;
    // лёд — диэлектрик с n ≈ 1.31 (F0 ≈ 0.018)
    surface.specular = lerp(0.018, 0.03, snow);
    surface.smoothness = smoothness;
    surface.occlusion = 1;
    surface.alpha = 1;
    surface.normalTS = half3(0, 0, 1);
    half3 lit = UniversalFragmentPBR(inputData, surface).rgb;

    // небо в льду и снегу — живое; берег и деревья в чистом льду — из копии кадра, как у воды
    half clear = (1 - snow) * (1 - dusting) * (1 - whiteIce) * (1 - crack);
    half4 mirror = 0;

    [branch] if (clear > 0.01)
        mirror = WaterScreenReflection(v.positionWS, reflect(-v.view, normalWS), v.distance);

    lit = WaterReflections(lit, surface, v.positionWS, v.view, normalWS, inputData.normalizedScreenSpaceUV, mirror, clear);

    lit = ApplyClouds(ApplyFogAndVolumetricLights(lit, v.screenUV, v.positionWS, 0), v.screenUV, v.positionWS);
    return half4(lit, saturate(depthHere * 6));
}

// Свет воды (URP, specular; прошедшее сквозь толщу — как свечение), прозрачность, туман и облака Enviro поверх.
// edge — мягкая кромка у берега, opacity — насколько непрозрачна толща, details — ряска и плёнка, foam — пена;
// farReflection: небо в отражении вдали слабее — (с какого расстояния, сколько остаётся).
half4 WaterShade(Varyings input, WaterView v, half3 normalTS, half3 albedo, half3 specular, half smoothness, half occlusion,
    half3 transmitted, half edge, half opacity, half details, half foam, float2 farReflection)
{
    float3 up = normalize(input.normalWS);
    float3 normalWS = normalize(float3(normalTS.x, 0, normalTS.y) + up * normalTS.z);

    InputData inputData = (InputData)0;
    inputData.positionWS = v.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = v.view;
    inputData.shadowCoord = TransformWorldToShadowCoord(v.positionWS);
    inputData.bakedGI = SampleSH(normalWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = half4(1, 1, 1, 1);

    // под ярким небом дальние плёсы иначе белели
    half far = lerp(1, farReflection.y, saturate((v.distance - farReflection.x) / max(farReflection.x * 2, 1)));

    SurfaceData surface = (SurfaceData)0;
    surface.albedo = albedo;
    surface.specular = specular * far;
    surface.smoothness = smoothness;
    // ряска и плёнка — не вода: небо в них вдали не гасим
    surface.occlusion = occlusion * lerp(far, 1, details);
    surface.emission = transmitted;
    surface.alpha = 1;
    surface.normalTS = half3(0, 0, 1);
    half3 lit = UniversalFragmentPBR(inputData, surface).rgb;

    // берег и деревья в отражении — из копии кадра там, где луч в неё упёрся (вместо неба пробы под ними); луч — по
    // наполовину сглаженной нормали, чтобы отражение дрожало, а не рассыпалось. Зарябившая вода (поток винтов, ветер) —
    // не зеркало: там отражение размыто, и чёткому из кадра веры меньше
    half4 mirror = WaterScreenReflection(v.positionWS, reflect(-v.view, normalize(lerp(up, normalWS, 0.5))), v.distance);
    lit = WaterReflections(lit, surface, v.positionWS, v.view, normalWS, inputData.normalizedScreenSpaceUV, mirror,
        saturate((smoothness - 0.6) / 0.3));

    // без копии кадра — обычная прозрачность: сквозь мель видно дно, под косым взглядом вода отражает
    half alpha = edge;

    if (_WaterRefraction < 0.5)
    {
        half fresnel = pow(1 - saturate(dot(normalWS, v.view)), 5);
        alpha *= saturate(max(lerp(0.35, 1, opacity), max(fresnel, details)));
    }

    alpha = saturate(max(alpha, foam));
    lit = ApplyClouds(ApplyFogAndVolumetricLights(lit, v.screenUV, v.positionWS, 0), v.screenUV, v.positionWS);
    return half4(lit, alpha);
}

#endif
