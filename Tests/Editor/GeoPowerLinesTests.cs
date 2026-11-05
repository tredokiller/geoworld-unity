using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // Power lines of a battlefield's settlements (GeoPowerLines): poles along streets of houses only on a battlefield, on one
    // side of the street, and wires hung from what is left of them.
    public class GeoPowerLinesTests
    {
        readonly List<Object> disposable = new List<Object>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in disposable) if (o) Object.DestroyImmediate(o);
            disposable.Clear();
        }

        static GenerationSettings Settings() => new GenerationSettings { trees = false, vegetation = false, mappedProps = true, maxProps = 5000 };

        // a 400 m street with houses on both sides, the tile ending at x = 300
        static void Street(out EnvironmentData env, out BuildingsData buildings, bool battlefield)
        {
            env = new EnvironmentData { bounds = new GeoBounds { minX = 0, minZ = 0, maxX = 300, maxZ = 300 }, battlefield = battlefield };
            env.surround = new GeoBounds { minX = -96, minZ = -96, maxX = 396, maxZ = 396 };
            env.roads.Add(new LineFeature { id = "street", type = "residential", kind = "road", width = 6, points = new float[] { -50, 150, 380, 150 } });
            buildings = new BuildingsData();
            for (int i = 0; i < 30; i++)
                foreach (float z in new[] { 135f, 165f })
                    buildings.buildings.Add(new BuildingFeature { id = "house/" + i + "/" + z, category = "house", x = -40 + i * 14, z = z, width = 8, length = 8,
                        footprint = new float[] { -4, -4, 4, -4, 4, 4, -4, 4 } });
        }

        [Test] public void Peace_HasNoPowerLines()
        {
            Street(out var env, out var buildings, false);
            var plan = GeoPropPlanner.Plan(env, buildings, Settings(), null, new List<string>(), null, true, new List<PropPlacement>());
            Assert.That(plan.Count(p => p.kind == "power_pole"), Is.EqualTo(0));
        }

        [Test] public void Battlefield_PolesAlongOneSideOfTheStreet()
        {
            Street(out var env, out var buildings, true);
            var beyond = new List<PropPlacement>();
            var plan = GeoPropPlanner.Plan(env, buildings, Settings(), null, new List<string>(), null, true, beyond);
            var poles = plan.Where(p => p.kind == "power_pole").OrderBy(p => p.position.x).ToList();
            Assert.That(poles.Count, Is.InRange(6, 10), "300 m of street, a pole every 30-38 m");
            Assert.That(poles.All(p => p.source == "generated"));
            float side = Mathf.Sign(poles[0].position.z - 150);
            Assert.That(poles.All(p => Mathf.Sign(p.position.z - 150) == side), "One side of the street.");
            Assert.That(poles.All(p => Mathf.Abs(Mathf.Abs(p.position.z - 150) - 4.8f) < 0.01f), "1.8 m past the edge of a 6 m street.");
            for (int i = 1; i < poles.Count; i++)
                Assert.That(poles[i].position.x - poles[i - 1].position.x, Is.InRange(25f, 45f));
            Assert.That(beyond.Count, Is.GreaterThan(0), "The poles past the tile are known for the wires.");
            Assert.That(beyond.All(p => p.position.x > 300 || p.position.x < 0));
            // the same plan every time
            var again = GeoPropPlanner.Plan(env, buildings, Settings(), null, new List<string>(), null, true, new List<PropPlacement>());
            Assert.That(again.Where(p => p.kind == "power_pole").Select(p => p.id + p.position), Is.EqualTo(plan.Where(p => p.kind == "power_pole").Select(p => p.id + p.position)));
        }

        [Test] public void Wires_HangBetweenPolesAndFallWithABrokenOne()
        {
            Street(out var env, out var buildings, true);
            var beyond = new List<PropPlacement>();
            var plan = GeoPropPlanner.Plan(env, buildings, Settings(), null, new List<string>(), null, true, beyond);
            var field = new HeightField(new GeoBounds { minX = -96, minZ = -96, maxX = 396, maxZ = 396 }, 65);
            var group = new GameObject("Wires test").transform; disposable.Add(group.gameObject);
            var notes = new List<string>();
            int spans = GeoPowerLines.Build(plan, null, beyond, null, null, field, group, 100, o => disposable.Add(o), 137, notes);
            int poles = plan.Count(p => p.kind == "power_pole");
            Assert.That(spans, Is.EqualTo(poles), "A span from every pole in the tile to the next, the last one into the next tile.");
            var renderers = group.GetComponentsInChildren<MeshRenderer>();
            Assert.That(renderers.Length, Is.GreaterThan(0));
            Assert.That(renderers.All(r => r.sharedMaterial && r.sharedMaterial.shader.name == GeoPowerLines.ShaderName));
            float low = renderers.SelectMany(r => r.GetComponent<MeshFilter>().sharedMesh.vertices).Min(v => v.y);
            Assert.That(low, Is.GreaterThan(5), "Intact wires hang well above the street.");

            // a pole snapped: the wires of its spans reach the ground
            var broken = plan.Where(p => p.kind == "power_pole").OrderBy(p => p.position.x).ElementAt(3);
            broken.damage = GeoPropDamage.Broken; broken.yaw = 0; broken.lean = 0;
            var group2 = new GameObject("Wires test 2").transform; disposable.Add(group2.gameObject);
            GeoPowerLines.Build(plan, null, beyond, null, null, field, group2, 100, o => disposable.Add(o), 137, new List<string>());
            var vertices = group2.GetComponentsInChildren<MeshFilter>().SelectMany(f => f.sharedMesh.vertices).ToList();
            var near = vertices.Where(v => Mathf.Abs(v.x - broken.position.x) < 15).ToList();
            Assert.That(near.Max(v => v.y), Is.LessThan(0.2f), "Only wires on the ground by the broken pole.");
            Assert.That(vertices.Where(v => Mathf.Abs(v.x - broken.position.x) < 45).Max(v => v.y), Is.GreaterThan(6), "and still on its standing neighbours.");
            Assert.IsTrue(notes.Any(n => n.Contains("Power lines")), string.Join("\n", notes));
        }
    }
}
