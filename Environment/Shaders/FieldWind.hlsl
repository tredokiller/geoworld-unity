// Порывы ветра бегут волнами по траве, посевам и камышу — как по пшенице: полосы поперёк ветра шириной в десятки метров
// катятся по ветру, трава в них сильнее клонится и светлеет (видна светлая изнанка листьев и стеблей). У NM_Wind порывы
// качают каждое растение, но волной по полю не идут. Здесь одно поле порывов на мир: по пробегу ветра игры (VegetationWind:
// _WaterWind — куда и с какой скоростью, _WaterWindRun — накопленный пробег), поэтому волны не прыгают, когда ветер
// меняется, и одинаковы для травы (RotorWash.hlsl → NM_Foliage: наклон в вершинах, светлее в пикселях) и для земли
// (GroundWetness.hlsl: зелёный луг и молодые посевы за дальностью травы — та же волна светлее).
#ifndef GAME_FIELD_WIND_INCLUDED
#define GAME_FIELD_WIND_INCLUDED

#ifndef GAME_WATER_WIND_GLOBALS
#define GAME_WATER_WIND_GLOBALS
// ветер игры (VegetationWind): xy — куда дует (мир xz), z — скорость, м/с
float4 _WaterWind;
// пробег ветра (м, мир xz): копится по кадрам
float4 _WaterWindRun;
#endif

float FieldWindHash(float2 cell, uint salt)
{
    int2 id = (int2)floor(cell);
    return GenerateHashedRandomFloat(uint3(asuint(id.x), asuint(id.y), salt));
}

float FieldWindNoise(float2 p, uint salt)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = FieldWindHash(i, salt), b = FieldWindHash(i + float2(1, 0), salt);
    float c = FieldWindHash(i + float2(0, 1), salt), d = FieldWindHash(i + 1, salt);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// Сила ветра для волн (0 — штиль, 1 — ~10 м/с и сильнее).
float FieldWindStrength()
{
    return saturate((_WaterWind.z - 0.8) / 9.0);
}

// Волна порыва в точке (0..1, без учёта силы ветра): полосы ~11 м вдоль ветра и ~30 м поперёк, бегут по ветру со
// скоростью ветра ×1.3 (порыв обгоняет средний ветер), с мелкой рябью поверх.
float FieldWindWave(float2 xz)
{
    float2 wind = _WaterWind.xy;
    float2 across = float2(-wind.y, wind.x);
    float2 p = xz - _WaterWindRun.xy * 1.3;
    float along = dot(p, wind);
    float side = dot(xz, across);
    float n = FieldWindNoise(float2(along / 11.0, side / 30.0), 811u) * 0.7 + FieldWindNoise(float2(along / 4.0, side / 9.0) + 7.3, 812u) * 0.3;
    return smoothstep(0.36, 0.8, n);
}

// Насколько растение у земли в волне (0..1) с учётом силы ветра.
float FieldWindGust(float2 xz)
{
    float strength = FieldWindStrength();

    if (strength <= 0.001 || dot(_WaterWind.xy, _WaterWind.xy) < 0.01)
        return 0.0;

    return FieldWindWave(xz) * (0.35 + 0.65 * strength);
}

// Наклон по ветру в мире: на высоте height над корнем, stiffness — 1 у травы, меньше у жёсткой стерни.
float3 FieldWindBend(float3 positionWS, float3 pivotWS, float stiffness)
{
    float gust = FieldWindGust(pivotWS.xz);

    if (gust <= 0.001)
        return positionWS;

    float height = max(0.0, positionWS.y - pivotWS.y);
    // клонится верх: к корню почти ничего; до 2.5 м (камыш), выше — деревья, их не трогаем
    float bend = gust * stiffness * height * height * 0.55 * (1.0 - smoothstep(2.5, 3.5, height));
    positionWS.xz += _WaterWind.xy * bend;
    positionWS.y -= bend * bend * 0.4;
    return positionWS;
}

// Цвет в волне: светлее и серебристее (изнанка листьев и стебли к камере), между волнами чуть темнее (трава стоит, видны
// тени между стеблями) — так полосы видно и с высоты, а средняя яркость луга почти не меняется.
half3 FieldWindSheen(half3 colour, float2 xz, half amount)
{
    float strength = FieldWindStrength();

    if (strength <= 0.001 || amount <= 0.001 || dot(_WaterWind.xy, _WaterWind.xy) < 0.01)
        return colour;

    half gust = (half)(FieldWindWave(xz) * (0.35 + 0.65 * strength)) * amount;
    half trough = (half)strength * amount * 0.06;
    half luminance = dot(colour, half3(0.299, 0.587, 0.114));
    half3 sheen = lerp(colour, luminance.xxx * half3(1.0, 1.02, 1.0), 0.45) * 1.4;
    return lerp(colour * (1.0 - trough), sheen, gust);
}

#endif
