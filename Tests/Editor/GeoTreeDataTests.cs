using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using System.Collections;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace GeoWorld.Editor.Tests
{
    public class GeoTreeDataTests
    {
        const string RuntimeTestPath = "Assets/Resources/GeoWorldTreeLifecycleTest.asset";

        [TearDown]
        public void CleanupRuntimeAsset()
        {
            AssetDatabase.DeleteAsset(RuntimeTestPath);
            AssetDatabase.DeleteAsset("Assets/GeoWorldStreamingTest.unity");
            AssetDatabase.DeleteAsset("Assets/GeoWorldStreamingMasterTest.unity");
        }

        [UnityTest]
        public IEnumerator RuntimeTrees_DisableAndReloadRestoresInstances()
        {
            System.IO.Directory.CreateDirectory("Assets/Resources");
            AssetDatabase.Refresh();
            var data = ScriptableObject.CreateInstance<GeoTreeData>();
            data.batches = new[] { new GeoInstancedMeshes.Batch { matrices = new[] { Matrix4x4.identity } } };
            data.instanceCount = 1;
            AssetDatabase.CreateAsset(data, RuntimeTestPath);
            AssetDatabase.SaveAssets();
            var previous = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(previous.path))
            {
                if (previous.rootCount != 0 && !Application.isBatchMode)
                    Assert.Ignore("Save the open scene before running the streaming lifecycle test.");
                EditorSceneManager.SaveScene(previous, "Assets/GeoWorldStreamingMasterTest.unity");
            }
            var tileScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var terrainGo = new GameObject("Runtime tree lifecycle");
            SceneManager.MoveGameObjectToScene(terrainGo, tileScene);
            terrainGo.AddComponent<Terrain>();
            terrainGo.AddComponent<GeoTerrainTreeLoader>().resourceKey = "GeoWorldTreeLifecycleTest";
            EditorSceneManager.SaveScene(tileScene, "Assets/GeoWorldStreamingTest.unity");
            EditorSceneManager.CloseScene(tileScene, true);
            SceneManager.SetActiveScene(previous);
            yield return new EnterPlayMode();
            var go = new GameObject("Streaming lifecycle test");
            var world = go.AddComponent<GeoTiledWorld>();
            world.tiles.Add(new GeoTiledWorld.Tile
            {
                scenePath = "Assets/GeoWorldStreamingTest.unity", area = new Rect(-50, -50, 100, 100)
            });
            var streamer = go.AddComponent<GeoTileStreamer>();
            streamer.world = world;
            streamer.target = go.transform;
            streamer.loadRadius = 100;
            streamer.unloadRadius = 200;
            for (int reload = 0; reload < 2; reload++)
            {
                go.transform.position = Vector3.zero;
                for (int step = 0; step < 100 && streamer.LoadedTiles == 0; step++)
                    yield return new WaitForSecondsRealtime(.1f);
                Assert.That(streamer.LoadedTiles, Is.EqualTo(1));
                var scene = SceneManager.GetSceneByPath("Assets/GeoWorldStreamingTest.unity");
                var loader = scene.GetRootGameObjects()[0].GetComponent<GeoTerrainTreeLoader>();
                for (int frame = 0; frame < 300 && !loader.Ready; frame++) yield return null;
                Assert.That(loader.Ready, Is.True, "Tree data must become ready after loading.");
                Assert.That(loader.GetComponentInChildren<GeoInstancedMeshes>().count, Is.EqualTo(1));
                go.transform.position = new Vector3(1000, 0, 1000);
                for (int step = 0; step < 100 && scene.isLoaded; step++)
                    yield return new WaitForSecondsRealtime(.1f);
                Assert.That(scene.isLoaded, Is.False);
                Assert.That(streamer.LoadedTiles, Is.Zero);
            }
            Object.Destroy(go);
            yield return new ExitPlayMode();
        }

        [Test]
        public void TreeData_RoundTripPreservesEveryTerrainTreeField()
        {
            var data = ScriptableObject.CreateInstance<GeoTreeData>();
            try
            {
                var tree = new TreeInstance
                {
                    position = new Vector3(.23f, .42f, .87f), rotation = 1.72f,
                    widthScale = .8f, heightScale = 1.4f, prototypeIndex = 3,
                    color = new Color32(12, 34, 56, 78), lightmapColor = new Color32(87, 65, 43, 21)
                };
                data.SetTrees("Terrain", new[] { tree });
                var copy = data.GetTreeInstance(0);
                Assert.That(copy.position, Is.EqualTo(tree.position));
                Assert.That(copy.rotation, Is.EqualTo(tree.rotation));
                Assert.That(copy.widthScale, Is.EqualTo(tree.widthScale));
                Assert.That(copy.heightScale, Is.EqualTo(tree.heightScale));
                Assert.That(copy.prototypeIndex, Is.EqualTo(tree.prototypeIndex));
                Assert.That(copy.color, Is.EqualTo(tree.color));
                Assert.That(copy.lightmapColor, Is.EqualTo(tree.lightmapColor));
                Assert.Throws<ArgumentOutOfRangeException>(() => data.GetTreeInstance(1));
                data.SetTrees("Terrain", Array.Empty<TreeInstance>());
                Assert.That(data.TreeCount, Is.Zero);
            }
            finally { Object.DestroyImmediate(data); }
        }

        [Test]
        public void Export_SeparatesTreesAndRebuildsInPlaceWithoutChangingGuid()
        {
            var go = new GameObject("Tree export test");
            var root = go.AddComponent<GeoWorldRoot>();
            root.assetPath = "Assets/Test" + Guid.NewGuid().ToString("N") + "/Tiles/World.asset";
            var terrainGo = new GameObject("Terrain");
            terrainGo.transform.SetParent(go.transform);
            root.terrain = terrainGo.AddComponent<Terrain>();
            var terrainData = new TerrainData();
            root.terrain.terrainData = terrainData;
            var instances = new GameObject("Instances").AddComponent<GeoInstancedMeshes>();
            instances.transform.SetParent(go.transform);
            instances.batches.Add(new GeoInstancedMeshes.Batch { matrices = new[] { Matrix4x4.identity } });
            instances.count = 1;
            string key = GeoTreeDataExporter.ResourceKey(root), path = "Assets/Resources/" + key + ".asset";
            try
            {
                GeoTreeDataExporter.Export(root);
                string guid = AssetDatabase.AssetPathToGUID(path);
                var data = AssetDatabase.LoadAssetAtPath<GeoTreeData>(path);
                Assert.That(data.instanceCount, Is.EqualTo(1));
                Assert.That(data.batches[0].matrices.Length, Is.EqualTo(1));
                Assert.That(root.GetComponentsInChildren<GeoInstancedMeshes>(), Is.Empty);
                Assert.That(root.terrain.GetComponent<GeoTerrainTreeLoader>().resourceKey, Is.EqualTo(key));
                // A new generation with no trees clears stale data in the same asset.
                GeoTreeDataExporter.Export(root);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(data.batches, Is.Empty);
                root.assetPath = "Assets/AnotherWorld/Tiles/World.asset";
                Assert.That(GeoTreeDataExporter.ResourceKey(root), Is.Not.EqualTo(key));
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(terrainData);
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
