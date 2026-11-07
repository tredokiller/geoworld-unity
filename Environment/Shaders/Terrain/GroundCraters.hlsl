// Воронки на земле (война): по карте воронок вокруг камеры (CraterMap, _CraterMap: CraterMap.shader — что в каналах).
// Крупные воронки врезаны в сетку высот тайла (GeoCombatGround) и видны геометрией и её нормалями; мелкие (уже сетки) —
// только здесь: их рельеф из окна (0.25 м на тексель) наклоняет нормаль. У всех: чаша темнее (свет неба в неё попадает
// меньше, земля в ней сырее), у свежих — светлый вывороченный грунт вокруг, в воронках с водой — вода прудка: мутная,
// глинисто-бурая (у каждой воды свой вид), гладь отражает живое небо (PuddleSky); в дождь вода набирается и в сухих
// воронках, зимой — подо льдом. Карта читается сэмплером sampler_LinearClamp: своих сэмплеров у прохода террейна нет.
#ifndef GAME_GROUND_CRATERS_INCLUDED
#define GAME_GROUND_CRATERS_INCLUDED

TEXTURE2D(_CraterMap);
// R — минус «чашность» воронок с водой, G — воронок и окопов, которых нет на снимке
TEXTURE2D(_CraterWater);
// xy — начало окна в мире (x, z), z — 1/размер окна, w — текселей по стороне (0 — карты нет)
float4 _CraterMapRect;

float4 SampleCraterMap(float2 uv)
{
    return SAMPLE_TEXTURE2D_LOD(_CraterMap, sampler_LinearClamp, uv, 0);
}

// высота рельефа мелких воронок в текселе (м): самая глубокая чаша, а вал — только вне чаш (как в генераторе)
float CraterRelief(float4 m)
{
    return m.r - m.g * exp(m.r / 0.15);
}

// bowl — насколько точка в чаше (0..1), pond — доля воды прудка в пикселе (для живого неба в отражении)
void GroundCraters(inout InputData inputData, float footprint, float distanceToCamera, inout half3 albedo, inout half smoothness, inout half occlusion,
    out half bowl, out half pond)
{
    bowl = 0;
    pond = 0;

    [branch] if (_CraterMapRect.w > 0.5)
    {
        float3 positionWS = inputData.positionWS;
        float2 uv = (positionWS.xz - _CraterMapRect.xy) * _CraterMapRect.z;
        float2 edge = min(uv, 1 - uv);
        half inside = saturate(min(edge.x, edge.y) * 16);

        [branch] if (inside > 0)
        {
            float4 m = SampleCraterMap(uv);
            bowl = saturate(-m.b) * inside;
            half2 w = SAMPLE_TEXTURE2D_LOD(_CraterWater, sampler_LinearClamp, uv, 0).rg;
            half waterBowl = saturate(-w.r) * inside;
            // чаша, которой нет на снимке: у найденных на нём их темнота уже в спутнике, и вторая делала из них чёрные дыры
            half darkBowl = saturate(-w.g) * inside;
            half ejecta = saturate(-m.a) * inside;

            [branch] if (bowl > 0.001 || ejecta > 0.001 || m.r < -0.001 || m.g < -0.001)
            {
                // рельеф мелких: нормаль по разностям через тексель; когда пиксель накрывает больше двух текселей — гаснет
                float texel = 1 / (_CraterMapRect.z * _CraterMapRect.w);
                half detail = (1 - smoothstep(texel, texel * 3, footprint)) * inside;

                [branch] if (detail > 0.001)
                {
                    float2 du = float2(_CraterMapRect.z * texel, 0);
                    float2 dv = float2(0, _CraterMapRect.z * texel);
                    float hx = CraterRelief(SampleCraterMap(uv + du)) - CraterRelief(SampleCraterMap(uv - du));
                    float hz = CraterRelief(SampleCraterMap(uv + dv)) - CraterRelief(SampleCraterMap(uv - dv));
                    float2 slope = float2(hx, hz) / (2 * texel) * detail;
                    inputData.normalWS = normalize(inputData.normalWS + float3(-slope.x, 0, -slope.y));
                    // мелкая чаша — в своей тени
                    occlusion *= saturate(1 + CraterRelief(m) * 0.6 * detail);
                }

                // в чаше света неба меньше и земля сырее (темнее); у свежих вокруг — светлый вывороченный грунт. Чаши,
                // видные на снимке, — только чуть: их тень и сырость уже в нём
                occlusion *= 1 - 0.35 * darkBowl - 0.12 * (bowl - darkBowl);
                albedo *= lerp(1, 0.82, darkBowl);
                half3 spoil = dot(albedo, half3(0.3, 0.59, 0.11)) * half3(1.2, 1.1, 0.95) + half3(0.02, 0.018, 0.012);
                albedo = lerp(albedo, spoil, ejecta * 0.35);

                // вода: в воронке с водой — середина чаши (до 0.7 радиуса), в дождь набирается и в остальных
                // a dry bowl, its shore noise included, stays under the wet rim until puddles are well on (~0.2 of the level)
                half rain = lerp(-0.32, 0.6, saturate(_PuddleLevel));
                half level = max(waterBowl > 0.001 ? waterBowl + 0.5 : 0, bowl + rain);
                [branch] if (level > 0.8)
                {
                    // берег не по циркулю: уровень гуляет шумом в двух масштабах — заливы по полтора метра и кромка по полметра
                    // (CPU — GroundWetness.WaterAt, тот же шум)
                    float2 p = positionWS.xz;
                    half shore = level + (PuddleNoise(p / 1.6 + 37.1) - 0.5) * 0.28 + (PuddleNoise(p / 0.5 + 11.3) - 0.5) * 0.08;
                    pond = smoothstep(0.97, 1.01, shore);
                    // мокрая грязь полосой до воды: темнее и с блеском, у самой кромки почти чёрная
                    half rim = smoothstep(0.86, 0.99, shore) * (1 - pond);
                    albedo *= 1 - 0.5 * rim;
                    smoothness = max(smoothness, rim * 0.6);

                    half frozen = _Season.w;
                    half iceSmoothness = 0.9, iceSnow = 0;
                    half3 ice = 0;

                    [branch] if (frozen > 0.5)
                        ice = GroundIce(positionWS.xz, footprint, iceSmoothness, iceSnow);

                    // прудок: у берега мелко, сквозь воду темнеет дно; дальше мутная глинистая вода, бурая или в зелень по
                    // пятнам ила; редкие пятна плёнки и сора — тусклые. Одна ровная заливка читалась сверху серым диском
                    half deep = smoothstep(0.98, 1.08, shore);
                    half silt = PuddleNoise(p / 1.7 + 5.3);
                    half3 muddy = lerp(half3(0.034, 0.03, 0.018), half3(0.046, 0.044, 0.022), silt);
                    half3 pondWater = lerp(albedo * 0.25 + 0.005, muddy, deep);
                    half scum = smoothstep(0.7, 0.82, PuddleNoise(p / 0.45 + 3.1) * 0.7 + silt * 0.3) * (1 - frozen);
                    pondWater = lerp(pondWater, half3(0.085, 0.078, 0.05), scum * 0.7);
                    albedo = lerp(albedo, lerp(pondWater, ice, frozen), pond);

                    // гладь не плоское зеркало: рябь от ветра (вблизи), кольца дождя и рябь под дроном, как у луж
                    half foam;
                    float near = saturate((120 - distanceToCamera) / 60);
                    float2 q = p / 0.35 + _Time.y * float2(0.5, 0.2);
                    float h = PuddleNoise(q);
                    float2 wind = float2(PuddleNoise(q + float2(0.2, 0)) - h, PuddleNoise(q + float2(0, 0.2)) - h) * 0.5 * near;
                    float2 slope = (wind + WaterRippleSlope(positionWS, foam) + WaterRainSlope(positionWS, distanceToCamera, _WaterRain) * 1.4)
                                   * (1 - frozen) * (1 - scum);
                    half mirror = lerp(0.92, 0.82, saturate(distanceToCamera / 300));
                    smoothness = lerp(smoothness, lerp(lerp(mirror, 0.4, scum), iceSmoothness, frozen), pond);
                    // низко над водой в ней отражаются стенки воронки, а не небо: чаша закрывает его ниже ~30°
                    half skyOpen = lerp(0.35, 1, smoothstep(0.2, 0.55, inputData.viewDirectionWS.y));
                    occlusion = lerp(occlusion, skyOpen, pond);
                    float3 still = normalize(lerp(inputData.normalWS, float3(0, 1, 0), pond));
                    inputData.normalWS = normalize(still + float3(-slope.x, 0, -slope.y) * pond);
                    pond *= (1 - frozen) * (1 - scum);
                }
            }
        }
    }
}

#endif
