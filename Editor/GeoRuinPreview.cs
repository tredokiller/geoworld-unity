using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Pictures of a ruin model fitted to footprints of other sizes, side by side on a plain ground, rendered in a preview
    // scene of their own (the open scene is not touched). The model Inspector uses it; so can a script that wants to
    // check every model (RenderToFile).
    public static class GeoRuinPreview
    {
        // One footprint to fit the model to: length and width in metres, total height, roof rise (0 flat).
        [Serializable]
        public struct Size
        {
            public float length, width, height, roofRise;
            public Size(float length, float width, float height, float roofRise) { this.length = length; this.width = width; this.height = height; this.roofRise = roofRise; }
        }

        // The model as modelled: its footprint and intact heights.
        public static Size Modelled(GeoRuinModel model) =>
            new Size(model.FootprintSize.x, model.FootprintSize.y, model.eaves + model.roofRise, model.roofRise);

        public static Texture2D Render(GeoRuinModel model, IList<Size> sizes, int width, int height, float yaw = 205, float pitch = 30)
        {
            if (!model || !model.Usable) throw new ArgumentException("The ruin model has no mesh or material.");
            var utility = new PreviewRenderUtility();
            var made = new List<Object>();
            try
            {
                var bounds = new Bounds();
                float x = 0;
                for (int i = 0; i < sizes.Count; i++)
                {
                    var size = sizes[i];
                    var outline = new List<Vector2> { new Vector2(-size.length / 2, -size.width / 2), new Vector2(size.length / 2, -size.width / 2), new Vector2(size.length / 2, size.width / 2), new Vector2(-size.length / 2, size.width / 2) };
                    var target = new GeoRuinFit.Target
                    {
                        frame = GeoRuinFit.FrameOf(outline), height = Mathf.Max(2, size.height), roofRise = Mathf.Max(0, size.roofRise),
                        eaves = Mathf.Max(2, size.height) - Mathf.Max(0, size.roofRise), apron = model.apron
                    };
                    var mesh = GeoRuinFit.Build(model, target, out _);
                    made.Add(mesh);
                    // in a row along -X: seen from the front (+Z), the first is on the left
                    float half = mesh.bounds.extents.x;
                    if (i > 0) x -= half + 4;
                    var go = EditorUtility.CreateGameObjectWithHideFlags("Fit " + i, HideFlags.HideAndDontSave, typeof(MeshFilter), typeof(MeshRenderer));
                    go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    go.GetComponent<MeshRenderer>().sharedMaterial = model.material;
                    go.transform.position = new Vector3(x - mesh.bounds.center.x, 0, 0);
                    utility.AddSingleGO(go);
                    made.Add(go);
                    var placed = new Bounds(mesh.bounds.center + go.transform.position, mesh.bounds.size);
                    if (i == 0) bounds = placed; else bounds.Encapsulate(placed);
                    x -= half;
                }
                // ground
                var groundMesh = new Mesh { name = "Preview ground" };
                float gx = bounds.extents.x + 20, gz = bounds.extents.z + 20;
                groundMesh.vertices = new[] { new Vector3(-gx, -0.02f, -gz), new Vector3(-gx, -0.02f, gz), new Vector3(gx, -0.02f, gz), new Vector3(gx, -0.02f, -gz) };
                groundMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                groundMesh.RecalculateNormals();
                var groundMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { color = new Color(0.42f, 0.42f, 0.4f) };
                if (groundMaterial.HasProperty("_BaseColor")) groundMaterial.SetColor("_BaseColor", new Color(0.42f, 0.42f, 0.4f));
                made.Add(groundMesh); made.Add(groundMaterial);
                var ground = EditorUtility.CreateGameObjectWithHideFlags("Preview ground", HideFlags.HideAndDontSave, typeof(MeshFilter), typeof(MeshRenderer));
                ground.GetComponent<MeshFilter>().sharedMesh = groundMesh;
                ground.GetComponent<MeshRenderer>().sharedMaterial = groundMaterial;
                ground.transform.position = new Vector3(bounds.center.x, 0, bounds.center.z);
                utility.AddSingleGO(ground);
                made.Add(ground);

                var camera = utility.camera;
                camera.fieldOfView = 28; camera.nearClipPlane = 0.5f; camera.farClipPlane = 5000;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.62f, 0.68f, 0.74f);
                // close enough that every corner of the row is just inside the picture
                var rotation = Quaternion.Euler(pitch, yaw, 0);
                var inverse = Quaternion.Inverse(rotation);
                float tanV = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad), tanH = tanV * width / Mathf.Max(1f, height);
                float distance = 5;
                for (int corner = 0; corner < 8; corner++)
                {
                    var p = inverse * (new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x, (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z) - bounds.center);
                    distance = Mathf.Max(distance, Mathf.Abs(p.x) / tanH - p.z, Mathf.Abs(p.y) / tanV - p.z);
                }
                camera.transform.SetPositionAndRotation(bounds.center - rotation * Vector3.forward * distance * 1.04f, rotation);
                utility.lights[0].intensity = 1.6f; utility.lights[0].transform.rotation = Quaternion.Euler(50, yaw - 50, 0);
                utility.lights[0].shadows = LightShadows.Soft;
                utility.lights[1].intensity = 0.6f; utility.lights[1].transform.rotation = Quaternion.Euler(25, yaw + 140, 0);
                utility.ambientColor = new Color(0.55f, 0.58f, 0.62f);
                utility.BeginStaticPreview(new Rect(0, 0, width, height));
                utility.Render(true);
                return utility.EndStaticPreview();
            }
            finally
            {
                utility.Cleanup();
                foreach (var o in made) if (o) Object.DestroyImmediate(o);
            }
        }

        // Renders the model and a few fits into a PNG (for checking a whole set of models from a script).
        public static void RenderToFile(GeoRuinModel model, IList<Size> sizes, string path, int width = 1600, int height = 700, float yaw = 205, float pitch = 30)
        {
            var texture = Render(model, sizes, width, height, yaw, pitch);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }

    // The ruin model Inspector: its settings, whether its joints make sense, and a picture of it next to the same model
    // fitted to a footprint of your choosing.
    [CustomEditor(typeof(GeoRuinModel))]
    public class GeoRuinModelEditor : UnityEditor.Editor
    {
        GeoRuinPreview.Size fit;
        bool sized;
        Texture2D picture;
        string error;

        public override void OnInspectorGUI()
        {
            var model = (GeoRuinModel)target;
            if (!sized) { fit = GeoRuinPreview.Modelled(model); fit.length *= 1.5f; sized = true; }
            if (!model.Usable) EditorGUILayout.HelpBox("The model has no mesh, material or footprint. Tools > Geo World > Import ruin models brings it in.", MessageType.Warning);
            foreach (var axis in new[] { ("Length", model.length, model.footprintMin.x, model.footprintMax.x), ("Width", model.width, model.footprintMin.y, model.footprintMax.y), ("Height", model.height, 0f, model.eaves) })
                if (!GeoRuinFit.IsValid(axis.Item2, axis.Item3, axis.Item4))
                    EditorGUILayout.HelpBox(axis.Item1 + ": the joints must rise inside the footprint (" + axis.Item3.ToString("F1") + " to " + axis.Item4.ToString("F1") +
                                            " m) and Segments needs one letter (C, F or R) more than there are joints. Until then the model is only stretched that way.", MessageType.Warning);
            DrawDefaultInspector();
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Fit preview", EditorStyles.boldLabel);
            fit.length = EditorGUILayout.Slider("Length (m)", fit.length, 2, 300);
            fit.width = EditorGUILayout.Slider("Width (m)", fit.width, 2, 100);
            fit.height = EditorGUILayout.Slider("Height (m)", fit.height, 2, 80);
            fit.roofRise = EditorGUILayout.Slider("Roof rise (m)", fit.roofRise, 0, Mathf.Max(0, fit.height - 2));
            if (model.Usable)
            {
                var outline = new List<Vector2> { new Vector2(0, 0), new Vector2(fit.length, 0), new Vector2(fit.length, fit.width), new Vector2(0, fit.width) };
                var measured = GeoRuinFit.Measure(model, new GeoRuinFit.Target { frame = GeoRuinFit.FrameOf(outline), height = fit.height, roofRise = fit.roofRise, eaves = fit.height - fit.roofRise });
                EditorGUILayout.HelpBox(measured.ToString(), MessageType.None);
            }
            using (new EditorGUI.DisabledScope(!model.Usable))
                if (GUILayout.Button("Render fit preview"))
                {
                    try
                    {
                        if (picture) DestroyImmediate(picture);
                        picture = GeoRuinPreview.Render(model, new[] { GeoRuinPreview.Modelled(model), fit }, 1024, 512);
                        error = null;
                    }
                    catch (ArgumentException e) { error = e.Message; }
                }
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (picture)
            {
                var rect = GUILayoutUtility.GetAspectRect(picture.width / (float)picture.height);
                GUI.DrawTexture(rect, picture, ScaleMode.ScaleToFit);
                EditorGUILayout.LabelField("Left: as modelled. Right: fitted to the size above.", EditorStyles.miniLabel);
            }
        }

        void OnDisable() { if (picture) DestroyImmediate(picture); }
    }
}
