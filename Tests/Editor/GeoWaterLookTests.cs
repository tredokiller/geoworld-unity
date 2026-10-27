using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // The water of every tile is drawn with the material they share (GeoWorld/Water: no mirror reflections of the sky), also in a tile made
    // before it, which carries a Lit material of its own.
    public class GeoWaterLookTests
    {
        [Test] public void WaterOfATile_TakesTheSharedMaterial_AndNothingElseDoes()
        {
            var root = new GameObject("tile");
            var lit = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "River water" };
            try
            {
                var group = new GameObject(GeoWaterLook.Group); group.transform.SetParent(root.transform);
                var water = new GameObject("Water | rivers"); water.transform.SetParent(group.transform);
                water.AddComponent<MeshFilter>(); water.AddComponent<MeshRenderer>().sharedMaterial = lit;
                var house = new GameObject("House"); house.transform.SetParent(root.transform);
                house.AddComponent<MeshFilter>(); house.AddComponent<MeshRenderer>().sharedMaterial = lit;

                Assert.That(GeoWaterLook.Material, Is.Not.Null, "The package ships the water material in Resources/" + GeoWaterLook.Resource + ".");
                Assert.That(GeoWaterLook.Material.shader.name, Is.EqualTo("GeoWorld/Water"));
                Assert.That(GeoWaterLook.Apply(root.transform), Is.EqualTo(1));
                Assert.That(water.GetComponent<MeshRenderer>().sharedMaterial, Is.EqualTo(GeoWaterLook.Material));
                Assert.That(house.GetComponent<MeshRenderer>().sharedMaterial, Is.EqualTo(lit), "Only the water group is touched.");
                Assert.That(GeoWaterLook.Apply(root.transform), Is.Zero, "Nothing to change the second time.");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(lit); }
        }

        [Test] public void TileWithoutWater_IsLeftAlone()
        {
            var root = new GameObject("tile");
            try { Assert.That(GeoWaterLook.Apply(root.transform), Is.Zero); }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
