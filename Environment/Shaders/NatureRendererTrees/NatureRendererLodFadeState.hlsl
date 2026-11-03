#ifndef GAME_NATURE_RENDERER_LOD_FADE_STATE_INCLUDED
#define GAME_NATURE_RENDERER_LOD_FADE_STATE_INCLUDED

// Переход LOD экземпляра Nature Renderer (NatureRendererLodFade.hlsl): код из младших 9 бит мантиссы scale.x
// (Nature Renderer Instancing.compute, GameStoreLodFade) — биты 0–7: 0 — без перехода, иначе 1 + доля перехода × 254;
// бит 8 — входящий LOD (уходящий — 0). Пишет SetupNatureRenderer (Nature Renderer.templatex) в обеих стадиях.
static uint g_NatureLodFade = 0;

uint NatureLodFadeCode(float scaleX)
{
    return asuint(scaleX) & 0x1FFu;
}

#endif
