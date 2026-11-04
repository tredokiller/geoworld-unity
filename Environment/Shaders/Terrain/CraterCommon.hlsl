// Форма воронки относительно земли вокруг неё — та же, что генератор врезал в сетку высот (GeoCraterProfile, C#):
// чаша-параболоид до глубины в центре, вал с гребнем на 1.2 R и выброс грунта до 2.25 R; износ (0..1) засыпает чашу (до
// 0.55 глубины), опускает вал до 0.4 и разводит его шире. s — расстояние от центра в радиусах ямы.
#ifndef GAME_CRATER_COMMON_INCLUDED
#define GAME_CRATER_COMMON_INCLUDED

#define CRATER_REACH 2.25
#define CRATER_LIP_CREST 1.2

float CraterOffset(float depth, float age, float s)
{
    if (s >= CRATER_REACH)
        return 0;

    age = saturate(age);
    float d = max(0, depth) * (1 - 0.45 * age);
    float lip = 0.18 * max(0, depth) * (1 - 0.6 * age);
    float width = 0.28 * (1 + 0.8 * age);
    float bowl = s < 1 ? -d * (1 - s * s) : 0;
    float g = (s - CRATER_LIP_CREST) / width;
    float crest = lip * exp(-g * g) * smoothstep(0.7, 1, s);
    float apron = s > CRATER_LIP_CREST ? 0.35 * lip * (1 - smoothstep(CRATER_LIP_CREST, CRATER_REACH, s)) : 0.35 * lip * smoothstep(0.9, CRATER_LIP_CREST, s);
    return bowl + max(crest, apron);
}

// Сечение окопа — как GeoTrenchProfile (C#): ров до глубины (свежий — плоское дно и крутые стенки, старый — оплывшее V:
// сверху до полутора раз шире, на треть мельче) и брустверы выброшенного грунта по обе стороны с гребнем в ~0.9 м за
// краем. d — метры от осевой линии.
float TrenchHalfWidth(float width, float age) { return 0.5 * max(0.3, width) * (1 + 0.5 * saturate(age)); }

float TrenchOffset(float depth, float width, float age, float d)
{
    age = saturate(age);
    float half = TrenchHalfWidth(width, age);
    float floorDepth = max(0, depth) * (1 - 0.35 * age);
    float bank = 0.25 * max(0, depth) * (1 - 0.3 * age);

    if (d >= half + 2.4)
        return 0;

    float t = d / half;
    float groove = t < 1 ? -floorDepth * (1 - pow(t, 2 + 2 * (1 - age))) : 0;
    float g = (d - half - 0.9) / 0.75;
    float spoil = bank * exp(-g * g) * smoothstep(0.7 * half, half, d);
    return groove + spoil;
}

#endif
