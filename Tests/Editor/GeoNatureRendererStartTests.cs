using System.Collections;
using GeoWorld.Tests.Support;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // A tile's Nature Renderer starts once the tile's trees are in, every time the tile is switched on, and a tile's trees stay in
    // while it is switched off. (Run with a stand-in for Nature Renderer's component: Geo World finds it by name.)
    public class GeoNatureRendererStartTests
    {
        const string Key = "GeoWorldNatureRendererStartTest";
        const string DataPath = "Assets/Resources/" + Key + ".asset";
        const string MasterPath = "Assets/GeoWorldNatureRendererStartMasterTest.unity";

        [TearDown] public void Cleanup()
        {
            AssetDatabase.DeleteAsset(DataPath);
            AssetDatabase.DeleteAsset(MasterPath);
        }

        static void MakeTreeData(GeoTreeData data)
        {
            System.IO.Directory.CreateDirectory("Assets/Resources");
            AssetDatabase.Refresh();
            AssetDatabase.CreateAsset(data, DataPath);
            AssetDatabase.SaveAssets();
            // Play mode needs a scene that is saved
            var previous = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(previous.path))
            {
                if (previous.rootCount != 0 && !Application.isBatchMode) Assert.Ignore("Save the open scene before running this test.");
                EditorSceneManager.SaveScene(previous, MasterPath);
            }
        }

        static void MakeInstancedTreeData()
        {
            var data = ScriptableObject.CreateInstance<GeoTreeData>();
            data.batches = new[] { new GeoInstancedMeshes.Batch { matrices = new[] { Matrix4x4.identity } } };
            data.instanceCount = 1;
            MakeTreeData(data);
        }

        // a tile's terrain made as a scene loads it: all its components come on together, in their execution order
        static GameObject Tile()
        {
            var go = new GameObject("Nature Renderer start test");
            go.SetActive(false);
            var terrain = go.AddComponent<Terrain>();
            terrain.terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(100, 10, 100) };
            go.AddComponent<NatureRenderer>();
            go.AddComponent<GeoTerrainTreeLoader>().resourceKey = Key;
            go.AddComponent<GeoNatureRendererStart>();
            go.SetActive(true);
            return go;
        }

        [UnityTest] public IEnumerator NatureRendererStartsOnceTheTreesAreIn_EveryTimeTheTileIsSwitchedOn()
        {
            MakeInstancedTreeData();
            yield return new EnterPlayMode();
            var go = Tile();
            var natureRenderer = go.GetComponent<NatureRenderer>();
            var loader = go.GetComponent<GeoTerrainTreeLoader>();
            try
            {
                // loading: held until the trees are in
                Assert.That(natureRenderer.enabled, Is.False, "Nature Renderer waits for the tile's trees.");
                Assert.That(natureRenderer.starts, Is.Zero);
                for (int i = 0; i < 600 && !natureRenderer.enabled; i++) yield return null;
                Assert.That(natureRenderer.enabled, Is.True);
                Assert.That(loader.Ready, Is.True);
                Assert.That(natureRenderer.starts, Is.EqualTo(1));
                Assert.That(natureRenderer.treesInWhenStarted, Is.True, "It started before the trees were in.");

                // the tile is switched off and on again, as a game that culls tiles does: its trees come back through the loader, and
                // Nature Renderer must not start on the empty terrain data of the moment
                var terrain = go.GetComponent<Terrain>();
                for (int round = 0; round < 2; round++)
                {
                    go.SetActive(false);
                    yield return null; yield return null;
                    go.SetActive(true);
                    Assert.That(natureRenderer.enabled, Is.False, "Nature Renderer is held again while the tile comes back (round " + round + ").");
                    for (int i = 0; i < 600 && !natureRenderer.enabled; i++) yield return null;
                    Assert.That(natureRenderer.enabled, Is.True, "Nature Renderer starts again (round " + round + ").");
                    Assert.That(loader.Ready, Is.True);
                    Assert.That(natureRenderer.treesInWhenStarted, Is.True, "It started before the trees were in (round " + round + ").");
                    Assert.That(natureRenderer.starts, Is.EqualTo(2 + round));
                    Assert.That(natureRenderer.Terrain, Is.EqualTo(terrain), "It is told the terrain.");
                    Assert.That(natureRenderer.dataWhenStarted, Is.EqualTo(terrain.terrainData), "It is told the terrain data the tile has now.");
                }
            }
            finally { Object.Destroy(go); }
            yield return new ExitPlayMode();
        }

        // Nature Renderer's own default sets a terrain up only within its rendering distance (0 on a terrain Geo World made): the
        // trees of a tile were not drawn at all. Every tile's Nature Renderer draws where the tile is loaded.
        [UnityTest] public IEnumerator NatureRendererIsNotLeftToSetUpTerrainsOnlyNearTheCamera()
        {
            MakeInstancedTreeData();
            yield return new EnterPlayMode();
            var go = Tile();
            var natureRenderer = go.GetComponent<NatureRenderer>();
            try
            {
                Assert.That(natureRenderer.OnlyInitializeWithinRenderingDistance, Is.False);
                for (int i = 0; i < 600 && !natureRenderer.enabled; i++) yield return null;
                Assert.That(natureRenderer.OnlyInitializeWithinRenderingDistance, Is.False, "Still so when it starts.");
            }
            finally { Object.Destroy(go); }
            yield return new ExitPlayMode();
        }

        [UnityTest] public IEnumerator NatureRendererThatIsRunningWhenTheTreesCome_StartsAgainOnThem()
        {
            MakeInstancedTreeData();
            yield return new EnterPlayMode();
            var go = Tile();
            var natureRenderer = go.GetComponent<NatureRenderer>();
            try
            {
                for (int i = 0; i < 600 && !natureRenderer.enabled; i++) yield return null;
                // the trees of the tile come again: Nature Renderer, running on the old data, starts again on the new
                var start = go.GetComponent<GeoNatureRendererStart>();
                int restarts = natureRenderer.restarts;
                start.TreesChanged();
                Assert.That(natureRenderer.restarts, Is.EqualTo(restarts + 1));
                // and not while it is off: it reads the data when it starts
                natureRenderer.enabled = false;
                start.TreesChanged();
                Assert.That(natureRenderer.restarts, Is.EqualTo(restarts + 1));
            }
            finally { Object.Destroy(go); }
            yield return new ExitPlayMode();
        }

        // A tile whose tree data is missing (not regenerated, or left out of a build): the loader says so, and the terrain's Nature
        // Renderer starts all the same instead of waiting for trees that never come.
        [UnityTest] public IEnumerator MissingTreeData_DoesNotHoldNatureRendererForever()
        {
            MakeInstancedTreeData();
            AssetDatabase.DeleteAsset(DataPath); // a saved scene for Play mode, and no tree data under the key
            yield return new EnterPlayMode();
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Missing Geo World tree data"));
            var go = Tile();
            var natureRenderer = go.GetComponent<NatureRenderer>();
            var loader = go.GetComponent<GeoTerrainTreeLoader>();
            var start = go.GetComponent<GeoNatureRendererStart>();
            try
            {
                for (int i = 0; i < 600 && !natureRenderer.enabled; i++) yield return null;
                Assert.That(loader.Ready, Is.True, "The loader gives up on the trees it cannot have.");
                Assert.That(natureRenderer.enabled, Is.True, "Nature Renderer starts without them.");
                Assert.That(start.Drawing, Is.True);
                Assert.That(start.Waiting, Is.False);
            }
            finally { Object.Destroy(go); }
            yield return new ExitPlayMode();
        }

        [UnityTest] public IEnumerator TreeLoader_KeepsTheTreesWhileTheTileIsSwitchedOff_AndLetsThemGoWithTheTile()
        {
            var data = ScriptableObject.CreateInstance<GeoTreeData>();
            data.SetTrees("test", new[]
            {
                new TreeInstance { position = new Vector3(.3f, 0, .4f), widthScale = 1, heightScale = 1, color = Color.white, lightmapColor = Color.white },
                new TreeInstance { position = new Vector3(.6f, 0, .7f), widthScale = 1, heightScale = 1, color = Color.white, lightmapColor = Color.white }
            });
            MakeTreeData(data);
            yield return new EnterPlayMode();
            var source = new TerrainData { heightmapResolution = 33, size = new Vector3(100, 10, 100) };
            source.treePrototypes = new[] { new TreePrototype { prefab = GeoTerrainTrees.Prefabs()[0] } };
            var go = new GameObject("Tree loader test");
            go.SetActive(false);
            var terrain = go.AddComponent<Terrain>();
            terrain.terrainData = source;
            var loader = go.AddComponent<GeoTerrainTreeLoader>();
            loader.resourceKey = Key;
            go.SetActive(true);
            for (int i = 0; i < 600 && !loader.Ready; i++) yield return null;
            Assert.That(loader.Ready, Is.True);
            var running = terrain.terrainData;
            Assert.That(running, Is.Not.EqualTo(source), "The tile draws its trees from a copy of the terrain data.");
            Assert.That(running.treeInstanceCount, Is.EqualTo(2));

            // Switched off and on again, the tile has its trees at once, on the same terrain data: a Nature Renderer that drew them
            // holds that data, and draws nothing once it is destroyed.
            go.SetActive(false);
            yield return null; yield return null;
            go.SetActive(true);
            Assert.That(loader.Ready, Is.True, "Switched on again, the trees are still in.");
            Assert.That(terrain.terrainData, Is.EqualTo(running));
            Assert.That((bool)running, Is.True, "Switching the tile off must not destroy the data a Nature Renderer holds.");
            Assert.That(running.treeInstanceCount, Is.EqualTo(2));

            // the data goes with the tile
            Object.Destroy(go);
            yield return null;
            Assert.That((bool)running, Is.False, "The tile's own copy of the data is released when the tile is.");
            Object.Destroy(source);
            yield return new ExitPlayMode();
        }
    }
}
