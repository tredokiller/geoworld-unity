#ifndef GAME_NATURE_RENDERER_LOD_FADE_INCLUDED
#define GAME_NATURE_RENDERER_LOD_FADE_INCLUDED

// Плавная смена LOD у растений Nature Renderer. Nature Renderer переключает LOD деревьев (и деталей с LODGroup) скачком
// на пороге: крона дерева на глазах становилась другой моделью, у карточки — плоской. Его отбор (Nature Renderer
// Instancing.compute, патч GameLodFade) в полосе перед порогом (доля _GameLodFade.x его дальности) рисует экземпляр
// обоими LOD и пишет в младшие биты scale.x долю перехода t и сторону (NatureRendererLodFadeState.hlsl); здесь —
// вместо LODFadeCrossFade URP, которую шейдер графа зовёт в каждом проходе (Forward, GBuffer, ShadowCaster, DepthOnly,
// DepthNormals), — по одному порогу дизеринга экрана уходящий LOD оставляет пиксели с d ≥ t, входящий — с d < t:
// каждый пиксель рисует ровно один из двух, и по полосе один перетекает в другой. Включать до LODCrossFade.hlsl URP:
// его страж здесь уже стоит, и он не включается. У объектов не из Nature Renderer (LODGroup точки взлёта) — как у URP,
// по unity_LODFade.

#include "Packages/com.tredokiller.geoworld/Environment/Shaders/NatureRendererTrees/NatureRendererLodFadeState.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

#define UNIVERSAL_PIPELINE_LODCROSSFADE_INCLUDED

// у Nature Renderer ключевое слово LOD_FADE_CROSSFADE не включает никто: переход решает код экземпляра
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(LOD_FADE_CROSSFADE)
    #define LOD_FADE_CROSSFADE 1
#endif

float _DitheringTextureInvSize;

TEXTURE2D(_DitheringTexture);

half CopySign(half x, half s)
{
    return (s >= 0) ? abs(x) : -abs(x);
}

// Дизеринг экрана 0..1 (interleaved gradient noise): соседние пиксели расходятся, узор стоит на месте.
float NatureLodFadeDither(float2 pixel)
{
    return frac(52.9829189 * frac(dot(floor(pixel), float2(0.06711056, 0.00583715))));
}

void LODFadeCrossFade(float4 positionCS)
{
    #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
        uint level = g_NatureLodFade & 0xFFu;

        if (level == 0u)
            return;

        float t = (level - 1u) / 254.0;
        float d = NatureLodFadeDither(positionCS.xy);
        // входящий виден там, где d < t (при t = 0 — нигде), уходящий — где d ≥ t
        clip((g_NatureLodFade & 0x100u) != 0u ? t - d - 1e-5 : d - t);
    #else
        half2 uv = positionCS.xy * _DitheringTextureInvSize;
        half d = SAMPLE_TEXTURE2D(_DitheringTexture, sampler_PointRepeat, uv).a;
        d = unity_LODFade.x - CopySign(d, unity_LODFade.x);
        clip(d);
    #endif
}

#endif
