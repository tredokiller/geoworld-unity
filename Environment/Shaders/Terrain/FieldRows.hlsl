// Живые поля: рядки, технологические колеи и борозды по направлению поля — на спутниковом снимке поле с высоты плоское, а
// в FPV-видео над степью главное в кадре — его штриховка. Направление — по рисунку самого снимка поля, где он слабый — по
// длинной стороне поля OSM (A карты земли _PuddleLand / _PuddleFarLand, GroundWetness; поля — FieldAreas, запекает
// FieldAreasBaker), без текстур: всё процедурно по мировым метрам поперёк
// рядков, поэтому рисунок стоит на месте и одинаков на стыках тайлов. Что рисовать — по снимку под точкой:
//  - на любом поле: пары колей техники (колея ~0.45 м, колея от колеи 1.8 м) через ширину опрыскивателя 18–36 м — голая
//    укатанная земля, темнее, с полосой примятого; проходы сеялки ~6 м через один светлее и темнее;
//  - посев (зелень снимка; летом игры — уже убранное поле, стерня): полосы прохода комбайна (жатка 6–9 м), рядки
//    0.15–0.7 м (зерновые, подсолнух, кукуруза) с рельефом нормали вблизи;
//  - пашня (тёмная и голая земля): борозды ~0.4 м — рельеф нормали и тени в бороздах, гребни через ~1.7 м (проход плуга).
// Каждый рисунок гаснет к своему среднему, когда пиксель накрывает его период (по производным), — без муара и мерцания;
// за 1–2 км остаётся снимок. Параметры поля (ширины, фаза) — от его направления, так соседние поля разные.
// Подключается после Puddles.hlsl (карта, шум). Зовётся до поры года и снега: снег их покрывает.
#ifndef GAME_FIELD_ROWS_INCLUDED
#define GAME_FIELD_ROWS_INCLUDED

// окна карты земли — LandSize (GroundWetness) текселей по стороне
#define FIELD_LAND_SIZE 256

// 1 — рисунок полей выключен (для сравнения; глобальная, по умолчанию 0)
float _FieldRowsOff;

// Байт A карты (0 — не поле) в точке: ближнее окно, иначе дальнее. Без интерполяции: направления соседних полей не
// смешиваются в чужое.
uint FieldDirectionByte(float2 xz)
{
    if (_PuddleLandRect.w > 0.5)
    {
        float2 uv = (xz - _PuddleLandRect.xy) * _PuddleLandRect.z;

        if (all(uv > 0.0) && all(uv < 1.0))
            return (uint)round(LOAD_TEXTURE2D(_PuddleLand, (int2)(uv * FIELD_LAND_SIZE)).a * 255.0);
    }

    if (_PuddleFarRect.w > 0.5)
    {
        float2 uv = (xz - _PuddleFarRect.xy) * _PuddleFarRect.z;

        if (all(uv > 0.0) && all(uv < 1.0))
            return (uint)round(LOAD_TEXTURE2D(_PuddleFarLand, (int2)(uv * FIELD_LAND_SIZE)).a * 255.0);
    }

    return 0u;
}

float FieldHash(uint value, uint salt)
{
    return GenerateHashedRandomFloat(uint2(value, salt));
}

// Линии ширины width через период period по координате s, сглаженные по размеру пикселя fs: 1 на линии, 0 между; когда
// пиксель шире линии, сходится к доле линии в периоде (её средней видимости).
float FieldLines(float s, float period, float width, float fs)
{
    float d = abs(frac(s / period + 0.5) - 0.5) * period;
    float soft = max(fs, width * 0.25);
    float stripe = 1.0 - smoothstep(width * 0.5 - soft * 0.5, width * 0.5 + soft * 0.5, d);
    float mean = width / period;
    return lerp(stripe, mean, saturate(fs / width - 0.5));
}

// Рисунок поля: albedo — цвет снимка (линейный), normalWS — нормаль пикселя; greenness — зелень снимка (как в
// GroundWetness), fieldShare — пашня по карте земель (плавная, для края поля).
void FieldRows(float3 positionWS, float2 dpdx, float2 dpdy, half greenness, inout half3 albedo, inout float3 normalWS)
{
    // край поля — не ступенька карты (8 м): доля поля по четырём точкам вокруг, сдвинутым шумом
    float2 xz = positionWS.xz;
    float2 warp = (float2(PuddleNoise(xz / 23.0), PuddleNoise(xz / 23.0 + 13.1)) - 0.5) * 8.0;
    uint value = FieldDirectionByte(xz + warp);
    float presence = 0.0;
    presence += FieldDirectionByte(xz + warp + float2(3.0, 1.5)) > 0u ? 0.25 : 0.0;
    presence += FieldDirectionByte(xz + warp + float2(-1.5, 3.0)) > 0u ? 0.25 : 0.0;
    presence += FieldDirectionByte(xz + warp + float2(-3.0, -1.5)) > 0u ? 0.25 : 0.0;
    presence += FieldDirectionByte(xz + warp + float2(1.5, -3.0)) > 0u ? 0.25 : 0.0;

    if (value == 0u || presence <= 0.0 || _FieldRowsOff > 0.5)
        return;

    float bearing = (value - 1.0) / 254.0 * PI;
    float2 along = float2(sin(bearing), cos(bearing));
    float2 across = float2(along.y, -along.x);
    float s = dot(xz, across);
    // метров поперёк рядков в пикселе
    float fs = abs(dot(dpdx, across)) + abs(dot(dpdy, across)) + 1e-4;
    float distanceFade = 1.0 - smoothstep(900.0, 1800.0, distance(positionWS, _WorldSpaceCameraPos));
    float weight = presence * distanceFade;

    if (weight <= 0.001)
        return;

    float h1 = FieldHash(value, 71u), h2 = FieldHash(value, 72u), h3 = FieldHash(value, 73u);
    half luminance = dot(albedo, half3(0.299, 0.587, 0.114));
    half crop = smoothstep(0.0, 0.1, greenness);
    // пашня — тёмная и не зелёная
    half plough = (1.0 - crop) * (1.0 - smoothstep(0.06, 0.12, luminance));
    half brightness = 1.0;
    float tilt = 0.0;

    // колеи техники — на любом поле, весь сезон (их видно и по стерне, и по бороновании): пара колей (колея к колее
    // 1.8 м, колея 0.45 м) через ширину опрыскивателя 18–36 м — голая укатанная земля, темнее, у посева ещё и буре;
    // вокруг пары полоса примятого шириной ~3 м — заметна с высоты
    float spray = h1 < 0.25 ? 18.0 : h1 < 0.55 ? 24.0 : h1 < 0.8 ? 28.0 : 36.0;
    float u = s + h2 * spray;
    float tracks = FieldLines(u + 0.9, spray, 0.45, fs) + FieldLines(u - 0.9, spray, 0.45, fs);
    float band = FieldLines(u, spray, 3.2, fs);
    brightness -= tracks * 0.3 + band * 0.07;
    // проходы сеялки/бороны (~6 м): через один чуть светлее и темнее — штриховка поля с высоты
    float drill = (FieldLines(s + h3 * 23.0, 12.0, 6.0, fs) - 0.5) * 2.0;
    brightness += drill * 0.04;

    // посев или стерня: полосы прохода комбайна, рядки
    [branch] if (crop > 0.01)
    {
        // жатка комбайна: через проход светлее и темнее (солома легла в разные стороны)
        float header = lerp(6.0, 9.0, h3);
        float swath = (FieldLines(s + h2 * 37.0, header * 2.0, header, fs) - 0.5) * 2.0;
        // рядки: зерновые 0.15 м, иногда пропашные 0.45–0.7 м
        float rowSpacing = h3 < 0.7 ? 0.15 : lerp(0.45, 0.7, h1);
        float rowFade = saturate(1.0 - fs / (rowSpacing * 0.5));
        float row = cos(s * (2.0 * PI / rowSpacing));

        brightness += crop * (swath * 0.06 + row * 0.09 * rowFade);
        tilt += crop * sin(s * (2.0 * PI / rowSpacing)) * 0.3 * rowFade;
    }

    // пашня: борозды с тенью, гребни проходов плуга
    [branch] if (plough > 0.01)
    {
        float furrow = lerp(0.35, 0.45, h2);
        float furrowFade = saturate(1.0 - fs / (furrow * 0.5));
        float phase = s * (2.0 * PI / furrow);
        float ploughPass = FieldLines(s + h1 * 11.0, lerp(1.4, 1.9, h3), 0.3, fs);

        brightness += plough * ((cos(phase) * 0.16 - 0.04) * furrowFade + ploughPass * 0.12);
        tilt += plough * sin(phase) * 0.5 * furrowFade;
    }

    albedo *= lerp(1.0, brightness, weight);

    if (abs(tilt) > 0.0001)
        normalWS = normalize(normalWS + float3(across.x, 0.0, across.y) * (tilt * weight));
}

// Поле под снегом (зимой; GroundWetness после SnowCoverAt): show — доля земли, что проступает сквозь снег в точке,
// tilt — наклон нормали поперёк рядков (гребни зяби), acrossWS — поперёк рядков в мире. Зимний снимок степи с высоты —
// лоскутное одеяло, а не ровная белая равнина:
//  - озимь — почти ровно белая, по ней тёмные пары технологических колей;
//  - стерня торчит из снега — поле серо-соломенное, полосами прохода комбайна, с рядками вблизи;
//  - зябь (осенняя пахота, по снимку — тёмная и не зелёная земля, как у FieldRows) — снег в бороздах, гребни и проходы
//    плуга тёмными штрихами: самое тёмное поле;
//  - межи — граница поля с не-полем или с другим полем (иной байт карты): бурьян и кусты торчат рваной полосой.
// Озимь или стерня — по хешу поля, поэтому соседние поля разные, а одно поле одинаково по всей площади. Рисунки сходятся
// к своим средним, как у FieldRows, но не гаснут с расстоянием: с высоты поля и межи и нужны.
void FieldSnow(float3 positionWS, float2 dpdx, float2 dpdy, half greenness, half luminance, float footprint,
    out half show, out float tilt, out float2 acrossWS)
{
    show = 0;
    tilt = 0;
    acrossWS = float2(1, 0);

    if (_FieldRowsOff > 0.5)
        return;

    float2 xz = positionWS.xz;
    float2 warp = (float2(PuddleNoise(xz / 23.0), PuddleNoise(xz / 23.0 + 13.1)) - 0.5) * 8.0;
    float2 c = xz + warp;
    uint value = FieldDirectionByte(c);
    // межа ~4 м у камеры, шире там, где пиксель накрывает метры (с высоты её видно)
    float reach = clamp(footprint * 1.5, 2.0, 6.0);
    uint n0 = FieldDirectionByte(c + float2(0.9, 0.45) * reach);
    uint n1 = FieldDirectionByte(c + float2(-0.45, 0.9) * reach);
    uint n2 = FieldDirectionByte(c + float2(-0.9, -0.45) * reach);
    uint n3 = FieldDirectionByte(c + float2(0.45, -0.9) * reach);
    float fields = (value > 0u ? 1.0 : 0.0) + (n0 > 0u ? 1.0 : 0.0) + (n1 > 0u ? 1.0 : 0.0) + (n2 > 0u ? 1.0 : 0.0) + (n3 > 0u ? 1.0 : 0.0);

    if (fields < 0.5)
        return;

    float other = (n0 != value ? 1.0 : 0.0) + (n1 != value ? 1.0 : 0.0) + (n2 != value ? 1.0 : 0.0) + (n3 != value ? 1.0 : 0.0);
    // за ближним окном карты (8 м) — дальнее (32 м): там граница ступенчатая, межа слабее
    float2 nearUV = (xz - _PuddleLandRect.xy) * _PuddleLandRect.z;
    half nearMap = _PuddleLandRect.w > 0.5 && all(nearUV > 0.0) && all(nearUV < 1.0) ? 1.0 : 0.5;
    // бурьян пучками: рваная полоса, местами кусты гуще, местами межа тонет в снегу
    half tufts = smoothstep(0.25, 0.75, PuddleNoise(xz / 1.7 + 41.3)) * 0.6 + smoothstep(0.3, 0.7, PuddleNoise(xz / 9.0 + 3.7)) * 0.4;
    half margin = saturate(other * 0.5) * (0.3 + 0.35 * tufts) * nearMap;

    half inside = 0;

    [branch] if (value > 0u)
    {
        float bearing = (value - 1.0) / 254.0 * PI;
        float2 along = float2(sin(bearing), cos(bearing));
        float2 across = float2(along.y, -along.x);
        acrossWS = across;
        float s = dot(xz, across);
        float fs = abs(dot(dpdx, across)) + abs(dot(dpdy, across)) + 1e-4;

        // те же хеши, что у FieldRows: колеи и проходы на снегу там же, где на земле
        float h1 = FieldHash(value, 71u), h2 = FieldHash(value, 72u), h3 = FieldHash(value, 73u);
        half crop = smoothstep(0.0, 0.1, greenness);
        half plough = (1.0 - crop) * (1.0 - smoothstep(0.06, 0.12, luminance));
        half stubble = (1.0 - plough) * (FieldHash(value, 91u) < 0.5 ? 1.0 : 0.0);

        // колеи техники: снег в них продавлен до земли — на любом поле, с высоты пары тёмных линий через 18–36 м
        float spray = h1 < 0.25 ? 18.0 : h1 < 0.55 ? 24.0 : h1 < 0.8 ? 28.0 : 36.0;
        float u = s + h2 * spray;
        float tracks = FieldLines(u + 0.9, spray, 0.45, fs) + FieldLines(u - 0.9, spray, 0.45, fs);
        inside = tracks * 0.55;

        // надувы: где снега больше, земли видно меньше (пятна в десятки метров, вытянуты по ветру зимы)
        half drift = 0.65 + 0.7 * PuddleNoise(float2(dot(xz, float2(0.8, -0.6)) / 41.0, dot(xz, float2(0.6, 0.8)) / 13.0) + 7.9);

        [branch] if (stubble > 0.01)
        {
            float header = lerp(6.0, 9.0, h3);
            float swath = FieldLines(s + h2 * 37.0, header * 2.0, header, fs);
            float rowSpacing = h3 < 0.7 ? 0.15 : lerp(0.45, 0.7, h1);
            float rowFade = saturate(1.0 - fs / (rowSpacing * 0.5));
            float row = cos(s * (2.0 * PI / rowSpacing)) * 0.5 + 0.5;
            half stalks = (0.24 + swath * 0.1 + (row - 0.5) * 0.2 * rowFade) * drift;
            inside = max(inside, stubble * stalks);
        }

        [branch] if (plough > 0.01)
        {
            // гребни зяби над снегом в бороздах; проходы плуга — сваленные гребни темнее
            float furrow = lerp(0.35, 0.45, h2) * 2.0;
            float furrowFade = saturate(1.0 - fs / (furrow * 0.5));
            float phase = s * (2.0 * PI / furrow);
            float crest = lerp(0.35, smoothstep(0.2, 0.9, cos(phase)), furrowFade);
            float ploughPass = FieldLines(s + h1 * 11.0, lerp(1.4, 1.9, h3), 0.3, fs);
            half clods = (0.12 + crest * 0.32 + ploughPass * 0.15) * drift;
            inside = max(inside, plough * clods);
            tilt = plough * sin(phase) * 0.35 * furrowFade;
        }
    }

    // глубокий снег прячет больше, но и на полном покрове поля читаются
    show = saturate(max(inside, margin));
}

#endif
