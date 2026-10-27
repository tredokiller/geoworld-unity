using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoWorld.Editor
{
    // The runtime generation demo: a scene with GeoWorldRuntimeDemo (bounds, Generate, progress, packages), a package streamer, a
    // camera and a sun, and a standalone player of it with the runtime assets and the Building Extractor app.
    public static class GeoRuntimeDemoBuild
    {
        public const string ScenePath = GeoPaths.Project + "/Scenes/RuntimeGenerationDemo.unity";

        [MenuItem("Tools/Geo World/Runtime generation/Create demo scene", false, 210)]
        static void CreateMenu() => EditorSceneManager.OpenScene(CreateScene());

        public static string CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.8f; light.color = new Color(1, 0.94f, 0.83f);
            light.shadows = LightShadows.Soft; light.transform.rotation = Quaternion.Euler(48, -35, 0);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.56f, 0.65f, 0.73f); RenderSettings.ambientEquatorColor = new Color(0.49f, 0.53f, 0.48f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.32f, 0.28f); RenderSettings.fog = false;
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera"; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.10f, 0.16f, 0.20f);
            camera.fieldOfView = 48; camera.nearClipPlane = 0.3f; camera.farClipPlane = 15000;
            camera.gameObject.AddComponent<AudioListener>();
            var view = camera.gameObject.AddComponent<GeoPreviewCamera>();
            view.distance = 1500; view.moveSpeed = 200; view.Apply();
            var runtime = new GameObject("Geo World runtime");
            var streamer = runtime.AddComponent<GeoWorldPackageStreamer>();
            var demo = runtime.AddComponent<GeoWorldRuntimeDemo>();
            demo.streamer = streamer; demo.view = view;
            EditorSceneManager.SaveScene(scene, ScenePath);
            return ScenePath;
        }

        [MenuItem("Tools/Geo World/Runtime generation/Build demo player...", false, 211)]
        static void BuildMenu()
        {
            string path = EditorUtility.SaveFilePanel("Demo player", "", "GeoWorldRuntimeDemo", "exe");
            if (!string.IsNullOrEmpty(path)) Build(path);
        }

        public static BuildReport Build(string output)
        {
            GeoRuntimeSetup.Prepare();
            if (!File.Exists(ScenePath)) CreateScene();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.None
            });
            Debug.Log($"Geo World demo player: {report.summary.result}, {report.summary.totalSize / 1048576f:F0} MB, {report.summary.totalTime.TotalMinutes:F1} min -> {output}");
            return report;
        }

        // Batch: -executeMethod GeoWorld.Editor.GeoRuntimeDemoBuild.BatchBuild -geoOut <player.exe> [-geoNewScene]
        public static void BatchBuild()
        {
            try
            {
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-geoNewScene") >= 0) CreateScene();
                var report = Build(GeoRuntimeSetup.Arg("-geoOut"));
                EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
    }
}
