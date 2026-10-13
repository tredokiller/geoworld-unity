using System.Diagnostics;
using UnityEngine;

namespace GeoWorld
{
    // The home objects of one part of a merged city, made from a GeoHomeSet when the part loads instead of being saved
    // in the scene. A tile scene with its 2,000 homes saved as objects took a third of a second to read and switch on
    // in one frame when it streamed in; now the scene holds a few objects and, while playing, the homes are made a
    // couple of milliseconds' worth per frame. Each home is the same as before: an object with an LODGroup, its parts
    // the library's own meshes and materials with a transform. In the editor (not playing) they are made at once and
    // are never saved with the scene.
    [ExecuteAlways, DisallowMultipleComponent]
    public class GeoHomeInstances : MonoBehaviour
    {
        public GeoHomeSet set;

        // Milliseconds per frame all the parts of all loaded tiles together spend making homes while playing.
        public static float BudgetMs = 2;
        static int budgetFrame = -1;
        static double spentMs;

        const HideFlags Made = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild | HideFlags.NotEditable;
        int next;

        public bool Done => !set || next >= set.homes.Count;
        public int Count => set ? set.homes.Count : 0;

        void OnEnable()
        {
            // homes made earlier (before a script reload, or before the part was switched off) are kept
            next = 0;
            foreach (Transform child in transform) if ((child.gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0) next++;
            if (!Application.isPlaying) MakeAll();
        }

        void Update()
        {
            if (!Application.isPlaying || Done) return;
            if (budgetFrame != Time.frameCount) { budgetFrame = Time.frameCount; spentMs = 0; }
            if (spentMs >= BudgetMs) return;
            var clock = Stopwatch.StartNew();
            while (!Done && spentMs + clock.Elapsed.TotalMilliseconds < BudgetMs) Make(set.homes[next++]);
            spentMs += clock.Elapsed.TotalMilliseconds;
        }

        public void MakeAll()
        {
            while (!Done) Make(set.homes[next++]);
        }

        // Removes the homes made so far and makes them again from the set (after the set changed).
        public void Remake()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                if ((child.hideFlags & HideFlags.DontSaveInEditor) == 0) continue;
                if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
            }
            next = 0;
            if (!Application.isPlaying) MakeAll();
        }

        void Make(GeoHomeSet.Home data)
        {
            var home = new GameObject(data.name) { hideFlags = Made };
            home.transform.SetParent(transform, false);
            var renderers = new Renderer[data.parts.Length];
            for (int i = 0; i < data.parts.Length; i++)
            {
                var part = data.parts[i];
                var go = new GameObject(part.name) { hideFlags = Made };
                go.transform.SetParent(home.transform, false);
                go.transform.SetLocalPositionAndRotation(part.position, part.rotation);
                go.transform.localScale = part.scale;
                go.AddComponent<MeshFilter>().sharedMesh = part.mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = part.materials;
                renderers[i] = renderer;
            }
            var lod = home.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(set.cullHeight, renderers) });
            lod.RecalculateBounds();
        }
    }
}
