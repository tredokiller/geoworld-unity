using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // Street props after the battle (GeoPropDamage): nothing changes where there was no fighting; where there was, props
    // lean, break, go, and fences are cut into their spans, the same on every run.
    public class GeoPropDamageTests
    {
        static EnvironmentData Env(float size) => new EnvironmentData { bounds = new GeoBounds { minX = 0, minZ = 0, maxX = size, maxZ = size } };
        static GenerationSettings Settings() => new GenerationSettings { trees = false, vegetation = false, mappedProps = true, maxProps = 5000 };

        // a street of benches, signs, lamps and a long fence
        static EnvironmentData Street()
        {
            var env = Env(400);
            for (int i = 0; i < 40; i++)
            {
                string kind = i % 4 == 0 ? "bench" : i % 4 == 1 ? "bus_stop" : i % 4 == 2 ? "street_lamp" : "shelter";
                env.props.Add(new PropFeature { id = "prop/" + i, type = kind, x = 20 + i * 9, z = 200, rotation = 0, rotationSource = "tag" });
            }
            env.barriers.Add(new BarrierFeature { id = "fence", type = "fence", points = new float[] { 20, 230, 380, 230 } });
            env.barriers.Add(new BarrierFeature { id = "wall", type = "wall", points = new float[] { 20, 260, 380, 260 } });
            return env;
        }

        static void Shell(EnvironmentData env, float centreX, float centreZ, int count, float spread)
        {
            for (int i = 0; i < count; i++)
            {
                float a = i * 2.39996f, r = spread * Mathf.Sqrt((i + 0.5f) / count);
                env.craters.Add(new CraterFeature { x = centreX + Mathf.Cos(a) * r, z = centreZ + Mathf.Sin(a) * r, diameter = 4 + i % 5, depth = 1 });
            }
        }

        static List<PropPlacement> Plan(EnvironmentData env, BuildingsData buildings, out List<string> notes)
        {
            notes = new List<string>();
            var plan = GeoPropPlanner.Plan(env, buildings ?? new BuildingsData(), Settings(), null, notes, null, true);
            GeoPropDamage.Apply(plan, env, buildings ?? new BuildingsData(), Settings(), notes);
            return plan;
        }

        [Test] public void Peace_LeavesThePlanAsItWas()
        {
            var env = Street();
            var before = GeoPropPlanner.Plan(env, new BuildingsData(), Settings(), null, new List<string>(), null, true);
            var after = Plan(env, null, out var notes);
            Assert.That(after.Select(p => p.id), Is.EqualTo(before.Select(p => p.id)));
            Assert.That(after.All(p => p.damage == GeoPropDamage.Intact && p.lean == 0), "No damage without craters or damaged buildings.");
            Assert.IsFalse(notes.Any(n => n.Contains("after the battle")), string.Join("\n", notes));
            // an intact building around changes nothing either
            var buildings = new BuildingsData();
            buildings.buildings.Add(new BuildingFeature { id = "house", x = 100, z = 215, width = 8, length = 8, footprint = new float[] { -4, -4, 4, -4, 4, 4, -4, 4 },
                condition = new GeoBuildingCondition { state = "intact" } });
            var intact = Plan(env, buildings, out _);
            Assert.That(intact.Select(p => p.id), Is.EqualTo(before.Select(p => p.id)));
        }

        [Test] public void Battle_BreaksPropsTheSameEveryTime()
        {
            var env = Street();
            Shell(env, 200, 215, 120, 170);
            var plan = Plan(env, null, out var notes);
            var again = Plan(env, null, out _);
            Assert.That(again.Select(p => p.id + ":" + p.damage + ":" + p.lean.ToString("F3")), Is.EqualTo(plan.Select(p => p.id + ":" + p.damage + ":" + p.lean.ToString("F3"))));
            var street = plan.Where(p => !p.segment).ToList();
            Assert.That(street.Count, Is.LessThan(40), "Some props are gone.");
            Assert.That(street.Count(p => p.damage == GeoPropDamage.Damaged), Is.GreaterThan(3));
            Assert.That(street.Count(p => p.damage == GeoPropDamage.Broken), Is.GreaterThan(2));
            Assert.That(street.Count(p => p.damage == GeoPropDamage.Intact), Is.GreaterThan(0), "Not everything is broken.");
            Assert.IsTrue(notes.Any(n => n.Contains("after the battle")), string.Join("\n", notes));
            Assert.That(plan.Select(p => p.id).Distinct().Count(), Is.EqualTo(plan.Count), "Ids stay unique.");
        }

        [Test] public void Battle_CutsFencesIntoSpansAndWallsKeepTheirRubble()
        {
            var env = Street();
            Shell(env, 200, 245, 160, 190);
            var plan = Plan(env, null, out _);
            var fence = plan.Where(p => p.kind == "fence").ToList();
            Assert.That(fence.All(p => p.length <= 3.2f), "Fence pieces are cut into spans of about 2.5 m.");
            Assert.That(fence.Sum(p => p.length), Is.LessThan(360 - 5), "Some spans are gone.");
            Assert.That(fence.Count(p => p.damage == GeoPropDamage.Broken && p.lean > 70), Is.GreaterThan(3), "Spans lie flat.");
            Assert.That(fence.Count(p => p.damage == GeoPropDamage.Damaged && p.lean > 10 && p.lean < 40), Is.GreaterThan(3), "Spans lean.");
            var wall = plan.Where(p => p.kind == "wall").ToList();
            Assert.That(wall.Sum(p => p.length), Is.EqualTo(360).Within(0.01), "A wall is never gone: it leaves its rubble.");
            Assert.That(wall.Any(p => p.damage == GeoPropDamage.Rubble));
            // a breach is lower than the wall in its middle and has chunks at its foot
            var breach = wall.First(p => p.damage == GeoPropDamage.Broken && !(p.lean > 0));
            var drafts = GeoPropBaker.Segment(breach, Settings().decorationSeed);
            Assert.That(drafts.Count, Is.GreaterThanOrEqualTo(2), "Wall and rubble.");
            var concrete = drafts[GeoPropBuilder.Concrete];
            Assert.That(concrete.vertices.Min(v => v.y), Is.GreaterThanOrEqualTo(-0.001f));
            int low = 0;
            for (int i = 0; i < concrete.vertices.Count; i++) if (concrete.vertices[i].y > 0.01f && concrete.vertices[i].y < breach.height * 0.2f) low++;
            Assert.That(low, Is.GreaterThan(0), "The breach goes down nearly to the ground.");
        }

        [Test] public void Lean_TiltsTowardItsAzimuthAboutTheFoot()
        {
            var p = new PropPlacement { kind = "street_lamp", yaw = 30, lean = 20, leanToward = 90 };
            var up = GeoPropDamage.Rotation(p) * Vector3.up;
            Assert.That(Vector3.Angle(up, Vector3.up), Is.EqualTo(20).Within(0.01));
            Assert.That(up.x, Is.GreaterThan(0.3f), "Toward +x (azimuth 90).");
            Assert.That(Mathf.Abs(up.z), Is.LessThan(0.01f));
            // a bin lying on its side is lifted onto the ground, a leaning lamp keeps its foot in it
            var bin = new Bounds(new Vector3(0, 0.6f, 0), new Vector3(1.4f, 1.2f, 0.9f));
            var lying = new PropPlacement { kind = "waste_disposal", lean = 90, leanToward = 0 };
            float lift = GeoPropDamage.Lift(GeoPropDamage.Rotation(lying), bin, Vector3.one, lying.lean);
            Assert.That(lift, Is.EqualTo(0.45f - 0.03f).Within(0.01f));
            Assert.That(GeoPropDamage.Lift(GeoPropDamage.Rotation(p), bin, Vector3.one, p.lean), Is.EqualTo(0));
        }

        [Test] public void Ruins_DamageThePropsAroundThem()
        {
            var env = Street();
            var buildings = new BuildingsData();
            for (int i = 0; i < 8; i++)
                buildings.buildings.Add(new BuildingFeature { id = "ruin/" + i, x = 30 + i * 45, z = 185, width = 10, length = 10, footprint = new float[] { -5, -5, 5, -5, 5, 5, -5, 5 },
                    condition = new GeoBuildingCondition { state = "destroyed" } });
            var plan = Plan(env, buildings, out _);
            Assert.That(plan.Count(p => !p.segment && p.damage != GeoPropDamage.Intact) + (40 - plan.Count(p => !p.segment)), Is.GreaterThan(8),
                "Destroyed buildings by the street damage its props.");
        }
    }
}
