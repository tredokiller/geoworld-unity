using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // Homes after the battle (GeoHomeWear): worn copies of the library's materials where the fighting was, none elsewhere.
    public class GeoHomeWearTests
    {
        readonly List<Object> disposable = new List<Object>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in disposable) if (o) Object.DestroyImmediate(o);
            disposable.Clear();
        }

        [Test] public void Wear_OnlyWhereTheFightingWasAndOneCopyPerMaterialAndLevel()
        {
            var env = new EnvironmentData { bounds = new GeoBounds { minX = 0, minZ = 0, maxX = 1000, maxZ = 1000 } };
            for (int i = 0; i < 200; i++)
                env.craters.Add(new CraterFeature { x = 100 + (i % 20) * 4, z = 100 + (i / 20) * 4, diameter = 3, depth = 0.8f });
            var war = new GeoPropDamage.War(env, new BuildingsData());
            var wear = new GeoHomeWear(war, o => disposable.Add(o));
            Assume.That(wear.Active, "GeoWorld/Home is in the project");

            Assert.That(wear.Level(new Vector2(140, 120), GeoConditionState.Intact), Is.EqualTo(3), "In the thick of it.");
            Assert.That(wear.Level(new Vector2(900, 900), GeoConditionState.Intact), Is.EqualTo(0), "Far from it.");
            Assert.That(wear.Level(new Vector2(900, 900), GeoConditionState.Damaged), Is.EqualTo(2), "A damaged home is worn wherever it is.");

            var body = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "N01" };
            body.EnableKeyword("_NORMALMAP");
            disposable.Add(body);
            Assert.That(wear.Material(body, 0, false, 2.5f), Is.SameAs(body), "Level 0 keeps the library's material.");
            var worn = wear.Material(body, 3, false, 2.5f);
            Assert.That(worn, Is.Not.SameAs(body));
            Assert.That(worn.shader.name, Is.EqualTo(GeoHomeWear.ShaderName));
            Assert.That(worn.GetFloat("_Parallax"), Is.EqualTo(0.95f).Within(1e-4f));
            Assert.That(worn.GetFloat("_ClearCoatSmoothness"), Is.EqualTo(0.25f).Within(1e-4f), "Eaves in tens of metres.");
            Assert.That(worn.IsKeywordEnabled("_NORMALMAP"), "Keywords carried over.");
            Assert.That(wear.Material(body, 3, false, 2.5f), Is.SameAs(worn), "One copy per material and level.");
            var roof = wear.Material(body, 3, true, 2.5f);
            Assert.That(roof, Is.Not.SameAs(worn));
            Assert.That(roof.GetFloat("_ClearCoatMask"), Is.EqualTo(1f));
            Assert.That(wear.Note(), Does.Contain("Homes after the battle"));

            // no war: nothing is touched
            var peace = new GeoHomeWear(null, o => disposable.Add(o));
            Assert.IsFalse(peace.Active);
            Assert.That(peace.Level(new Vector2(140, 120), GeoConditionState.Destroyed), Is.EqualTo(0));
            Assert.That(peace.Material(body, 3, false, 2.5f), Is.SameAs(body));
        }
    }
}
