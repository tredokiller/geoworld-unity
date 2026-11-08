using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // The power poles' model (GeoPowerPole): wires on the insulators, the same wire on the same side of every pole, a broken
    // pole on the ground, a sane cost near and far, and its instances drawn one level of detail at a time.
    public class GeoPowerPoleTests
    {
        readonly List<Object> disposable = new List<Object>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in disposable) if (o) Object.DestroyImmediate(o);
            disposable.Clear();
        }

        static PropPlacement Pole(string id, Vector3 at, float yaw, int damage = GeoPropDamage.Intact) =>
            new PropPlacement { id = id, kind = "power_pole", source = "generated", position = at, yaw = yaw, damage = damage };

        [Test] public void Wires_AreTiedToTheInsulatorsNecks()
        {
            var set = new GeoPowerPole.Set(o => disposable.Add(o));
            var mesh = set.Of("weathered").near;
            var vertices = mesh.vertices;
            for (int i = 0; i < GeoPowerPole.Wires; i++)
            {
                var top = GeoPowerPole.WireTop(i);
                float nearest = vertices.Min(v => Vector3.Distance(v, top));
                Assert.That(nearest, Is.LessThan(0.03f), "Wire " + i + " at an insulator's neck.");
                Assert.That(top.y, Is.InRange(7.3f, 7.6f), "Over the crossarm, under the pole's top.");
                Assert.That(Mathf.Abs(top.x), Is.GreaterThan(GeoPowerPole.TopWidth / 2 + 0.04f), "Clear of the shaft.");
            }
            Assert.That(mesh.bounds.max.y, Is.EqualTo(GeoPowerPole.Height).Within(0.01f));
            Assert.That(mesh.bounds.min.y, Is.EqualTo(-GeoPowerPole.Buried).Within(0.01f), "Into the ground, so a slope shows no gap.");
        }

        [Test] public void Wires_KeepTheirSideWhenAPoleIsTurned()
        {
            // two poles along x (their local z along the line), one turned half round
            PropPlacement a = null, b = null;
            for (int i = 0; i < 200 && (a == null || b == null); i++)
            {
                var p = Pole("street/power/" + i, new Vector3(i * 32, 0, 0), 90);
                if (GeoPowerPole.Flipped(p, 7)) b ??= p; else a ??= p;
            }
            Assert.IsNotNull(a); Assert.IsNotNull(b);
            var ta = GeoPowerPole.WireTops(a, 7); var tb = GeoPowerPole.WireTops(b, 7);
            for (int i = 0; i < GeoPowerPole.Wires; i++)
                Assert.That(Mathf.Sign(ta[i].z), Is.EqualTo(Mathf.Sign(tb[i].z)), "Wire " + i + " on the same side of both poles.");
            Assert.That(ta.Select(t => Mathf.Round(t.z * 100)).Distinct().Count(), Is.EqualTo(GeoPowerPole.Wires), "Wires apart.");
        }

        [Test] public void BrokenPole_StumpStandsAndTheTopLiesOnTheGround()
        {
            var set = new GeoPowerPole.Set(o => disposable.Add(o));
            foreach (float variant in new[] { 0.125f, 0.875f })
            {
                string key = "broken " + variant.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                foreach (var mesh in new[] { set.Of(key).near, set.Of(key).far })
                {
                    var v = mesh.vertices;
                    var lying = v.Where(p => p.z > 0.45f).ToList();
                    Assert.That(lying.Count, Is.GreaterThan(0));
                    Assert.That(lying.Min(p => p.y), Is.InRange(-0.05f, 0.02f), "The top rests on the ground.");
                    Assert.That(lying.Max(p => p.y), Is.LessThan(0.9f), "and lies on it.");
                    Assert.That(lying.Max(p => p.z), Is.GreaterThan(4.5f), "along +z.");
                    Assert.That(v.Where(p => Mathf.Abs(p.z) < 0.12f && Mathf.Abs(p.x) < 0.13f).Max(p => p.y), Is.InRange(1.1f, 3.2f), "The stump.");
                }
                foreach (var top in GeoPowerPole.BrokenWireTops(variant))
                {
                    Assert.That(top.y, Is.LessThan(0.1f), "The wires torn off lie on the ground.");
                    Assert.That(top.z, Is.GreaterThan(4f), "by the fallen top.");
                }
            }
        }

        [Test] public void Shapes_CostLittleNearAndLessFar()
        {
            var set = new GeoPowerPole.Set(o => disposable.Add(o));
            foreach (var key in new[] { "weathered", "aged", "scarred", "broken 0.375" })
            {
                var shape = set.Of(key);
                int near = shape.near.triangles.Length / 3, far = shape.far.triangles.Length / 3;
                Assert.That(near, Is.InRange(300, 1100), key + " near");
                Assert.That(far, Is.LessThan(110), key + " far");
                Assert.That(shape.near.tangents.Length, Is.EqualTo(shape.near.vertexCount), "Tangents for the normal map.");
                Assert.That(shape.near.uv.All(u => u.x >= 0 && u.x <= 1 && u.y >= 0 && u.y <= 1));
                Assert.That(shape.near.normals.All(n => Mathf.Abs(n.magnitude - 1) < 0.01f));
            }
        }

        [Test] public void Plant_DrawsOneLevelOfDetailAtATime()
        {
            var parent = new GameObject("Poles test").transform; disposable.Add(parent.gameObject);
            var poles = new List<PropPlacement>();
            for (int i = 0; i < 12; i++) poles.Add(Pole("street/power/" + i, new Vector3(i * 33, 0, 5), 90, i == 4 ? GeoPropDamage.Broken : i == 6 ? GeoPropDamage.Damaged : GeoPropDamage.Intact));
            poles[6].lean = 14; poles[6].leanToward = 0;
            int planted = GeoPowerPole.Plant(poles, parent, o => disposable.Add(o), 7);
            Assert.That(planted, Is.EqualTo(12));
            var drawer = parent.GetComponentInChildren<GeoInstancedMeshes>();
            Assert.That(drawer.count, Is.EqualTo(12));
            Assert.That(drawer.drawDistance, Is.EqualTo(GeoPowerPole.Far));
            var near = drawer.batches.Where(b => b.far > 0).ToList(); var far = drawer.batches.Where(b => b.near > 0).ToList();
            Assert.That(near.Count, Is.EqualTo(far.Count));
            Assert.That(near.Sum(b => b.matrices.Length), Is.EqualTo(12));
            Assert.That(far.Sum(b => b.matrices.Length), Is.EqualTo(12));
            for (int i = 0; i < near.Count; i++)
            {
                Assert.That(far[i].bounds, Is.EqualTo(near[i].bounds), "Both levels by the same bounds.");
                Assert.That(near[i].far, Is.EqualTo(far[i].near));
            }
            Assert.That(drawer.batches.All(b => b.material && b.material.enableInstancing));
            Assert.That(drawer.batches.Select(b => b.material).Distinct().Count(), Is.EqualTo(1), "One material for every pole.");
            Assert.That(drawer.batches.Any(b => b.mesh.name.Contains("broken")) && drawer.batches.Any(b => b.mesh.name.Contains("scarred")));
        }

        [Test] public void LevelsOfDetail_SurviveATileFile()
        {
            var root = new GameObject("Tile root"); disposable.Add(root);
            var poles = Enumerable.Range(0, 5).Select(i => Pole("street/power/" + i, new Vector3(i * 33, 0, 5), 90)).ToList();
            GeoPowerPole.Plant(poles, root.transform, o => disposable.Add(o), 7);
            var bytes = GeoWorldPackageFormat.Write(root, new GeoTileHeader(), new GeoTileNavigation(), new List<string>());
            var data = GeoWorldPackageFormat.Read(bytes);
            // the tile's builder is internal to the runtime: build it as the package does
            var type = typeof(GeoWorldPackageFormat).GetNestedType("Builder", BindingFlags.NonPublic);
            var created = new List<Object>();
            var builder = System.Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { data, null, created, new GeoTileLoadOptions() }, null);
            var step = type.GetMethod("Step", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            while ((bool)step.Invoke(builder, null)) { }
            var loaded = (GameObject)type.GetProperty("Root").GetValue(builder);
            disposable.Add(loaded); disposable.AddRange(created);
            var source = root.GetComponentInChildren<GeoInstancedMeshes>();
            var copy = loaded.GetComponentInChildren<GeoInstancedMeshes>();
            Assert.IsNotNull(copy);
            Assert.That(copy.batches.Select(b => (b.near, b.far, b.matrices.Length)), Is.EqualTo(source.batches.Select(b => (b.near, b.far, b.matrices.Length))));
            Assert.That(copy.batches.All(b => b.mesh && b.material));
        }
    }
}
