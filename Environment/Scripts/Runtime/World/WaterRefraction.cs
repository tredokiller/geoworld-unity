using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Перед каждой камерой сообщает шейдерам воды (Game/Water/*), есть ли у неё копия кадра (_CameraOpaqueTexture):
    /// с ней вода преломляет дно, без неё (окно Scene, служебные камеры) рисуется обычной прозрачностью — иначе
    /// читала бы чужую или пустую текстуру. Копию кадра камере дрона включает ViewDialog.
    /// </summary>
    public static class WaterRefraction
    {
        private static readonly int RefractionId = Shader.PropertyToID("_WaterRefraction");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Subscribe()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            RenderPipelineManager.beginCameraRendering += BeginCamera;
        }

        private static void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            bool opaqueTexture;

            // GetUniversalAdditionalCameraData добавил бы компонент и камере окна Scene
            if (camera.TryGetComponent(out UniversalAdditionalCameraData data))
                opaqueTexture = data.requiresColorTexture;
            else
                opaqueTexture = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset && asset.supportsCameraOpaqueTexture;

            Shader.SetGlobalFloat(RefractionId, opaqueTexture ? 1f : 0f);
        }
    }
}
