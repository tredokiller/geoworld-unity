using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // What Prepare runtime assets puts in the registry a player loads: nothing a player has no script for (the editor's objects
    // URP keeps under materials), and every shader a player draws worlds with, the one that makes a terrain's normal map too.
    public class GeoRuntimeAssetsTests
    {
        [Test] public void Registry_LeavesOutTheEditorObjectUnderAMaterial()
        {
            var objects = AssetDatabase.LoadAllAssetsAtPath(GeoPaths.Package + "/Homes/N01/N01.mat");
            var material = objects.OfType<Material>().Single();
            var others = objects.Where(o => o && o != material).ToList();
            Assert.That(others.Select(o => o.GetType().FullName), Has.Member("UnityEditor.Rendering.Universal.AssetVersion"), "URP keeps its version under the material");
            Assert.That(GeoRuntimeSetup.Kept(material));
            foreach (var o in others) Assert.That(GeoRuntimeSetup.Kept(o), Is.False, o.GetType().FullName);
        }

        [Test] public void Registry_KeepsNoObjectOfAnEditorAssembly()
        {
            // the assemblies a player build compiles, and the engine's
            var player = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name).ToHashSet();
            int kept = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material t:ScriptableObject", new[] { GeoPaths.Package }))
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)))
                {
                    if (!o || !GeoRuntimeSetup.Kept(o)) continue;
                    string assembly = o.GetType().Assembly.GetName().Name;
                    Assert.That(assembly.StartsWith("UnityEngine") || player.Contains(assembly), o.GetType().FullName + " of " + assembly);
                    kept++;
                }
            Assert.That(kept, Is.GreaterThan(100), "the package's materials and libraries are kept");
        }

        [Test] public void InPlayer_TheEnginesAndThePlayersAssembliesOnly()
        {
            Assert.That(GeoRuntimeSetup.InPlayer(typeof(Material)));
            Assert.That(GeoRuntimeSetup.InPlayer(typeof(GeoRuntimeAssets)));
            Assert.That(GeoRuntimeSetup.InPlayer(typeof(GeoHomeLibrary)));
            Assert.That(GeoRuntimeSetup.InPlayer(typeof(EditorWindow)), Is.False);
            Assert.That(GeoRuntimeSetup.InPlayer(typeof(GeoRuntimeSetup)), Is.False);
        }

        [Test] public void Registry_HasTheShaderOfTheTerrainsNormalMap()
        {
            Assert.That(GeoRuntimeSetup.Shaders, Has.Member("Hidden/TerrainEngine/GenerateNormalmap"));
            foreach (var name in GeoRuntimeSetup.Shaders) Assert.That(Shader.Find(name), Is.Not.Null, name);
        }
    }
}
