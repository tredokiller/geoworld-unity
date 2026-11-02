// Общее у карты дорог (Hidden/Game/Road Map) и террейна (GroundRoads.hlsl): какая дорога в точке главная и как мерить до
// её отрезка. Внутри полотна побеждает дорога важнее (рельсы, асфальт и плитка, гравий, грунт, тропинка), при равной —
// та, в чьём полотне точка глубже; за полотнами — ближайшая кромка. В ROAD_YIELD м от конца, примкнувшего к другой
// дороге, и за концом дороги (круглый край у продолжения) полотно уступает любому другому: боковая дорога упирается в
// кромку главной, а не врезается в неё клином по биссектрисе, и её конец не выглядывает из главной. Тупик и примыкание
// обрезаны поперёк (круглый конец заворачивал колеи петлёй), тупик сходит на нет за последние ROAD_FREE_FADE м.
#ifndef GAME_ROAD_MAP_COMMON_INCLUDED
#define GAME_ROAD_MAP_COMMON_INCLUDED

// концы дороги (RoadAreas.End)
#define ROAD_END_ROUND 0
#define ROAD_END_FREE 1
#define ROAD_END_JOINED 2
#define ROAD_END_ATTACHED 3
// тупик: полотно сходит на нет от столько метров до конца до ROAD_FREE_BEYOND за ним
#define ROAD_FREE_FADE 4.0
#define ROAD_FREE_BEYOND 1.0
// примыкание: конец — на оси другой дороги, за ним ещё столько (там полотно всё равно её)
#define ROAD_ATTACHED_CAP 1.0
// примыкание: столько метров от конца дорога уступает другим
#define ROAD_YIELD 14.0

struct RoadMeasure
{
    // до оси, м (+ слева по ходу), и вдоль от A
    float distance;
    float across;
    float s;
    // доля полотна у тупика
    float fade;
    // меньше — главнее (полотно — от −600 до 0, за кромкой — метры до неё)
    float key;
    bool valid;
};

// важность покрытия: грунт 1, асфальт 3, плитка 3, гравий 2, тропинка 0, рельсы 4
float RoadRank(float kind)
{
    return kind > 4.5 ? 4 : kind > 3.5 ? 0 : kind > 2.5 ? 2 : kind > 0.5 ? 3 : 1;
}

// p — точка, a, b — отрезок (мир xz); info: x — полуширина, y — метры от начала дороги до A, z — вид + 8 × разметка +
// 16 × износ + 128 × бордюр + 256 × начало + 1024 × конец (RoadAreas.End), w — метры от A до конца дороги.
RoadMeasure MeasureRoad(float2 p, float2 a, float2 b, float4 info)
{
    RoadMeasure m;
    float2 ab = b - a;
    float len = length(ab);
    float2 dir = len > 1e-4 ? ab / len : float2(1, 0);
    float2 offset = p - a;
    m.s = dot(offset, dir);
    float side = dir.x * offset.y - dir.y * offset.x;
    float code = round(info.z);
    float kind = fmod(code, 8);
    float startEnd = fmod(floor(code / 256), 4);
    float endEnd = floor(code / 1024);
    float fromStart = info.y + m.s;
    float toEnd = info.w - m.s;
    bool flatStart = fromStart < 0 && (startEnd == ROAD_END_FREE || startEnd == ROAD_END_ATTACHED);
    bool flatEnd = toEnd < 0 && (endEnd == ROAD_END_FREE || endEnd == ROAD_END_ATTACHED);
    bool flat = flatStart || flatEnd;
    m.distance = flat ? abs(side) : distance(p, a + dir * clamp(m.s, 0, len));
    m.across = side >= 0 ? m.distance : -m.distance;

    float beyond = max(-fromStart, -toEnd);
    float capEnd = flatStart ? startEnd : endEnd;
    float cap = capEnd == ROAD_END_FREE ? ROAD_FREE_BEYOND : ROAD_ATTACHED_CAP;
    m.valid = !flat || beyond < cap;
    m.fade = (startEnd == ROAD_END_FREE ? smoothstep(-ROAD_FREE_BEYOND, ROAD_FREE_FADE, fromStart) : 1) *
             (endEnd == ROAD_END_FREE ? smoothstep(-ROAD_FREE_BEYOND, ROAD_FREE_FADE, toEnd) : 1);
    // за примкнувшим концом — полотно другой дороги
    m.fade *= flat && capEnd == ROAD_END_ATTACHED ? 1 - smoothstep(0, cap, beyond) : 1;

    bool yield = beyond > 0 || (startEnd == ROAD_END_ATTACHED && fromStart < ROAD_YIELD) || (endEnd == ROAD_END_ATTACHED && toEnd < ROAD_YIELD);
    float level = yield ? 0 : 1 + RoadRank(kind);
    float edge = m.distance - info.x;
    m.key = edge <= 0 ? edge - level * 100 : edge;
    return m;
}

#endif
