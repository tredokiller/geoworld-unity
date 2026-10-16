using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // A generation config keeps the window's settings in an asset: tree prefabs as references to the prefab assets,
    // so they are there again in the next editor session.
    public class GeoGenerationConfigTests
    {
        string folder;

        [SetUp] public void MakeFolder()
        {
            folder = GeoPaths.Project + "/Generated/ConfigTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }

        [TearDown] public void Cleanup() => AssetDatabase.DeleteAsset(folder);

        [Test] public void AConfigKeepsTheSettingsAndTreePrefabsInItsAsset()
        {
            var tree = AssetDatabase.LoadAssetAtPath<GameObject>(GeoTerrainTrees.PrefabPath(0));
            Assert.That(tree, Is.Not.Null);
            var config = ScriptableObject.CreateInstance<GeoGenerationConfig>();
            config.tiled = false; config.tileSize = 750;
            config.settings.treePrefabs = new[] { tree };
            config.settings.terrainShadows = true;
            config.settings.treeSpacing = 11;
            string path = folder + "/City.asset";
            AssetDatabase.CreateAsset(config, path);
            AssetDatabase.SaveAssets();

            // what is on disk: the prefab by its asset, and every setting
            var saved = new SerializedObject(AssetDatabase.LoadAssetAtPath<GeoGenerationConfig>(path));
            var prefabs = saved.FindProperty("settings.treePrefabs");
            Assert.That(prefabs.arraySize, Is.EqualTo(1));
            Assert.That(prefabs.GetArrayElementAtIndex(0).objectReferenceValue, Is.EqualTo(tree));
            Assert.That(saved.FindProperty("tiled").boolValue, Is.False);
            Assert.That(saved.FindProperty("tileSize").floatValue, Is.EqualTo(750));
            Assert.That(saved.FindProperty("settings.terrainShadows").boolValue);
            Assert.That(saved.FindProperty("settings.treeSpacing").floatValue, Is.EqualTo(11));
            string text = File.ReadAllText(path);
            if (text.StartsWith("%YAML")) Assert.That(text, Does.Contain(AssetDatabase.AssetPathToGUID(GeoTerrainTrees.PrefabPath(0))));
        }
    }
}
