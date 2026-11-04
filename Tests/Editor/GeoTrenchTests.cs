using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // Trenches in 3D: the mesh (GeoTrenchMesh) and the terrain cut under it (GeoCombatGround).
    public class GeoTrenchTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        // a field of 64 m at 1 m, sloping 6 % to the east, with a little relief
        static HeightField Field()
        {
            var field = new HeightField(new GeoBounds { minX = -32, minZ = -32, maxX = 32, maxZ = 32 }, 65);
            for (int z = 0; z < 65; z++)
                for (int x = 0; x < 65; x++)
                    field.heights[z, x] = 10 + 0.06f * field.X(x) + 0.15f * Mathf.Sin(field.X(x) * 0.7f) * Mathf.Cos(field.Z(z) * 0.5f);
            return field;
        }

        static HeightField Copy(HeightField field)
        {
            var copy = new HeightField(field.bounds, field.resolution);
            System.Array.Copy(field.heights, copy.heights, field.heights.Length);
            return copy;
        }

        static TrenchFeature Trench(int kind, float width, float depth, float age, params Vector2[] points) =>
            new TrenchFeature { points = points.SelectMany(p => new[] { p.x, p.y }).ToArray(), width = width, depth = depth, age = age, kind = kind };

        // a zigzag fire trench across the field and a communication trench leaving it to the north
        static List<TrenchFeature> Trenches() => new List<TrenchFeature>
        {
            Trench(GeoTrenchKind.Fire, 1f, 1.8f, 0.8f, new Vector2(-20, -1), new Vector2(-11, 1), new Vector2(-2, -1), new Vector2(7, 1), new Vector2(16, -1)),
            Trench(GeoTrenchKind.Communication, 0.9f, 1.5f, 0.8f, new Vector2(-2, -1), new Vector2(-1, 8), new Vector2(1, 17)),
        };

        // the heights where a ray straight down meets the mesh at p
        static List<float> Hits(IEnumerable<Mesh> meshes, Vector2 p)
        {
            var hits = new List<float>();
            foreach (var mesh in meshes)
            {
                var v = mesh.vertices;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var t = mesh.GetTriangles(s);
                    for (int i = 0; i < t.Length; i += 3)
                    {
                        Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                        // barycentric in the horizontal plane
                        float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                        if (Mathf.Abs(d) < 1e-9f) continue;
                        float wa = ((b.z - c.z) * (p.x - c.x) + (c.x - b.x) * (p.y - c.z)) / d;
                        float wb = ((c.z - a.z) * (p.x - c.x) + (a.x - c.x) * (p.y - c.z)) / d;
                        float wc = 1 - wa - wb;
                        if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f) continue;
                        hits.Add(wa * a.y + wb * b.y + wc * c.y);
                    }
                }
            }
            return hits;
        }

        static float Distance(TrenchFeature t, Vector2 p)
        {
            float best = float.MaxValue;
            for (int k = 0; k + 3 < t.points.Length; k += 2)
                best = Mathf.Min(best, GeoData.SegmentDistance(p, new Vector2(t.points[k], t.points[k + 1]), new Vector2(t.points[k + 2], t.points[k + 3]), out _));
            return best;
        }

        List<Mesh> Build(List<TrenchFeature> trenches, HeightField uncut, HeightField field, Rect area)
        {
            var chunks = GeoTrenchMesh.Build(trenches, uncut.Sample, field.Sample, area, new Rect(-32, -32, 64, 64), 1f, 100);
            foreach (var c in chunks) owned.Add(c.mesh);
            return chunks.Select(c => c.mesh).ToList();
        }

        [Test] public void Section_CapIsBelowTheSurfaceWithinACellDiagonal()
        {
            var s = new GeoTrenchSection(1f, 1.8f, 0.8f, GeoTrenchSection.LipWidth(1f));
            Assert.That(s.floorHalf, Is.LessThan(s.half)); Assert.That(s.depth, Is.GreaterThan(1f));
            float diagonal = Mathf.Sqrt(2);
            for (float d = 0; d < s.lipEnd + 2; d += 0.05f)
            {
                float cap = s.Cap(d, diagonal);
                if (float.IsPositiveInfinity(cap))
                {
                    // nothing to cut: all of a node's triangles lie past the slot, under the spoil and the apron
                    Assert.That(d - diagonal, Is.GreaterThanOrEqualTo(s.half - 1e-4f), $"node at {d:F2} m left standing by the slot");
                    continue;
                }
                for (float e = Mathf.Max(0, d - diagonal); e <= Mathf.Min(s.lipEnd, d + diagonal); e += 0.01f)
                    Assert.That(cap, Is.LessThanOrEqualTo(s.Surface(e) + 1e-4f), $"node at {d:F2} m, surface at {e:F2} m");
            }
            // the apron lies over every terrain triangle the cut lowers
            Assert.That(s.half + s.apron, Is.GreaterThan(s.half + 2 * diagonal));
        }

        // the spoil rises from the edge to a round crest and runs out into the ground at its toe with no height and no slope
        [Test] public void Section_SpoilRunsOutIntoTheGroundFlat()
        {
            const float crest = GeoTrenchSection.CrestShare;
            Assert.That(GeoTrenchSection.Profile(0, crest), Is.EqualTo(GeoTrenchSection.LipShare).Within(1e-5f));
            Assert.That(GeoTrenchSection.Profile(crest, crest), Is.EqualTo(1).Within(1e-5f));
            Assert.That(GeoTrenchSection.Profile(1, crest), Is.EqualTo(0).Within(1e-6f));
            Assert.That(GeoTrenchSection.Profile(1.3f, crest), Is.EqualTo(0));
            const float e = 0.001f;
            Assert.That((GeoTrenchSection.Profile(1, crest) - GeoTrenchSection.Profile(1 - e, crest)) / e, Is.EqualTo(0).Within(0.01f), "slope at the toe");
            Assert.That((GeoTrenchSection.Profile(crest + e, crest) - GeoTrenchSection.Profile(crest - e, crest)) / (2 * e), Is.EqualTo(0).Within(0.01f), "slope at the crest");
            float last = 1;
            for (float t = crest; t <= 1; t += 0.01f)
            {
                float f = GeoTrenchSection.Profile(t, crest);
                Assert.That(f, Is.LessThanOrEqualTo(last + 1e-6f), $"rises again at {t:F2}");
                last = f;
            }
        }

        // a spike (a short arm turning back on itself) is dropped from the centreline the mesh and the cut follow; a zigzag of
        // right angles keeps its corners
        [Test] public void Centreline_DropsSpikes()
        {
            var spike = Trench(GeoTrenchKind.Fire, 1f, 1.5f, 0f, new Vector2(0, 0), new Vector2(10, 0), new Vector2(13, -1), new Vector2(11, 2), new Vector2(11, 10));
            Assert.That(GeoTrenchMesh.Centreline(spike), Is.EqualTo(new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(11, 2), new Vector2(11, 10) }));
            var zigzag = Trench(GeoTrenchKind.Fire, 1f, 1.5f, 0f, new Vector2(0, 0), new Vector2(6, 0), new Vector2(6, 6), new Vector2(12, 6));
            Assert.That(GeoTrenchMesh.Centreline(zigzag).Count, Is.EqualTo(4));
        }

        // the terrain where a ray straight down meets it at p, the higher of a height cell's two ways into triangles
        static float TerrainAt(HeightField field, Vector2 p)
        {
            float fx = p.x + 32, fz = p.y + 32;
            int x = Mathf.FloorToInt(fx), z = Mathf.FloorToInt(fz);
            float u = fx - x, v = fz - z;
            float h00 = field.heights[z, x], h10 = field.heights[z, x + 1], h01 = field.heights[z + 1, x], h11 = field.heights[z + 1, x + 1];
            float one = u + v <= 1 ? h00 + (h10 - h00) * u + (h01 - h00) * v : h11 + (h01 - h11) * (1 - u) + (h10 - h11) * (1 - v);
            float other = u >= v ? h00 + (h10 - h00) * u + (h11 - h10) * v : h00 + (h01 - h00) * v + (h11 - h01) * u;
            return Mathf.Max(one, other);
        }

        [Test] public void Carve_KeepsEveryTerrainCornerUnderTheMesh()
        {
            var field = Field(); var uncut = Copy(field); var trenches = Trenches();
            var notes = new List<string>();
            GeoCombatGround.Carve(field, new List<CraterFeature>(), trenches, null, null, notes);
            Assert.That(notes.Single(), Does.Contain("Trenches: 2 cut under their slots"));
            var meshes = Build(trenches, uncut, field, new Rect(-32, -32, 64, 64));
            Assert.That(meshes, Is.Not.Empty);
            var random = new System.Random(5);
            int slot = 0, spoil = 0;
            for (int i = 0; i < 6000; i++)
            {
                var p = new Vector2((float)random.NextDouble() * 50 - 25, (float)random.NextDouble() * 30 - 8);
                // only where the mesh is meant to show (not where its edge turns down under the terrain)
                bool shown = trenches.Any(t => { var s = GeoTrenchSection.Of(t, 1f); return Distance(t, p) < s.half + s.apron - 0.05f; });
                if (!shown) continue;
                var hits = Hits(meshes, p);
                if (hits.Count == 0) continue;
                // the surface seen from above (another trench's edge may turn down under the terrain at the same place)
                float seen = hits.Max();
                bool inSlot = trenches.Any(t => Distance(t, p) < GeoTrenchSection.Of(t, 1f).half);
                if (inSlot)
                {
                    // every corner of the height cell under p (any triangle of it) stands below the slot
                    int x = Mathf.FloorToInt(p.x + 32), z = Mathf.FloorToInt(p.y + 32);
                    float corner = Mathf.Max(Mathf.Max(field.heights[z, x], field.heights[z, x + 1]), Mathf.Max(field.heights[z + 1, x], field.heights[z + 1, x + 1]));
                    Assert.That(corner, Is.LessThan(seen + 0.02f), $"terrain through the slot at {p}");
                    slot++;
                }
                else
                {
                    // past the slot the terrain keeps its height under the spoil and the apron, which stand above it
                    Assert.That(TerrainAt(field, p), Is.LessThan(seen), $"terrain through the spoil at {p}");
                    // (the mesh's triangles are flat between their corners, the ground under them bends a little)
                    Assert.That(seen, Is.GreaterThanOrEqualTo(uncut.Sample(p.x, p.y) + 0.5f * GeoTrenchMesh.Lift), $"spoil below the ground at {p}");
                    spoil++;
                }
            }
            Assert.That(slot, Is.GreaterThan(150));
            Assert.That(spoil, Is.GreaterThan(1000));
        }

        // at the zigzag's corners the outer side turns round, the inner side meets at the mitre: no ground triangle folds over
        // (faces down), and the communication trench, cut at the fire trench's floor, has no wall inside it
        [Test] public void Build_CornersAndJunctions_DoNotFold()
        {
            var field = Field(); var uncut = Copy(field); var trenches = Trenches();
            GeoCombatGround.Carve(field, new List<CraterFeature>(), trenches, null, null, new List<string>());
            var meshes = Build(trenches, uncut, field, new Rect(-32, -32, 64, 64));
            var fire = GeoTrenchSection.Of(trenches[0], 1f);
            var link = GeoTrenchSection.Of(trenches[1], 1f);
            int folded = 0, ground = 0, wallsInside = 0, slivers = 0;
            foreach (var mesh in meshes)
            {
                var v = mesh.vertices;
                var t = mesh.GetTriangles(0);
                for (int i = 0; i < t.Length; i += 3)
                {
                    var n = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                    if (n.sqrMagnitude < 1e-8f) continue;
                    ground++;
                    if (n.normalized.y < -0.05f) folded++;
                    // the ground standing on edge (points of neighbouring rows apart on one line, at an inner corner's mitre)
                    else if (n.normalized.y < 0.15f && n.magnitude > 2e-3f) slivers++;
                }
                var w = mesh.GetTriangles(1);
                for (int i = 0; i < w.Length; i += 3)
                {
                    var c = new Vector2((v[w[i]].x + v[w[i + 1]].x + v[w[i + 2]].x) / 3f, (v[w[i]].z + v[w[i + 1]].z + v[w[i + 2]].z) / 3f);
                    // no wall over either floor at the junction: the communication trench's walls do not reach into the fire
                    // trench's, the fire trench's do not stand across the communication trench's mouth
                    if (c.x < -3.5f || c.x > 0.5f) continue;
                    if (Distance(trenches[0], c) < fire.floorHalf - 0.05f || Distance(trenches[1], c) < link.floorHalf - 0.05f) wallsInside++;
                }
            }
            Assert.That(ground, Is.GreaterThan(100));
            Assert.That(folded, Is.EqualTo(0), "ground triangles turned over");
            Assert.That(slivers, Is.EqualTo(0), "ground triangles standing on edge");
            Assert.That(wallsInside, Is.EqualTo(0), "walls standing across the fire trench at the junction");
        }

        [Test] public void Build_SplitsAcrossTilesWithNothingLostOrTwice()
        {
            var field = Field(); var uncut = Copy(field); var trenches = Trenches();
            GeoCombatGround.Carve(field, new List<CraterFeature>(), trenches, null, null, new List<string>());
            long Count(IEnumerable<Mesh> meshes) => meshes.Sum(m => (long)(m.GetIndexCount(0) + m.GetIndexCount(1)));
            long whole = Count(Build(trenches, uncut, field, new Rect(-32, -32, 64, 64)));
            long west = Count(Build(trenches, uncut, field, new Rect(-32, -32, 32, 64)));
            long east = Count(Build(trenches, uncut, field, new Rect(0, -32, 32, 64)));
            Assert.That(west + east, Is.EqualTo(whole));
            Assert.That(west, Is.GreaterThan(0)); Assert.That(east, Is.GreaterThan(0));
        }

        [Test] public void Trenches_OpenIntoEachOther()
        {
            var field = Field(); var uncut = Copy(field); var trenches = Trenches();
            GeoCombatGround.Carve(field, new List<CraterFeature>(), trenches, null, null, new List<string>());
            var meshes = Build(trenches, uncut, field, new Rect(-32, -32, 64, 64));
            var fire = GeoTrenchSection.Of(trenches[0], 1f);
            // the communication trench's centreline just past the fire trench's wall: no lip or wall of the fire trench across
            // its mouth, the mesh there is down at a floor
            var mouth = new Vector2(-1.85f, -1 + fire.half + 0.2f);
            float ground = uncut.Sample(mouth.x, mouth.y);
            var hits = Hits(meshes, mouth);
            Assert.That(hits, Is.Not.Empty);
            Assert.That(hits.Max(), Is.LessThan(ground - 0.5f), "The fire trench's lip stands across the communication trench.");
        }

        [Test] public void GroundMaterial_DrawsAMeshWithTheTerrainsLayers()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(500, 50, 400) };
            owned.Add(data);
            var texture = new Texture2D(4, 4); owned.Add(texture);
            var layer = new TerrainLayer { diffuseTexture = texture, tileSize = new Vector2(500, 400), smoothness = 0.2f }; owned.Add(layer);
            data.terrainLayers = new[] { layer };
            var go = Terrain.CreateTerrainGameObject(data); owned.Add(go);
            var shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            Assume.That(shader, Is.Not.Null, "URP's terrain shader.");
            var template = new Material(shader) { enableInstancing = true }; owned.Add(template);
            template.EnableKeyword("_TERRAIN_INSTANCED_PERPIXEL_NORMAL");
            var material = GeoTrenchGround.GroundMaterial(go.GetComponent<Terrain>(), template); owned.Add(material);
            Assert.That(material.enableInstancing, Is.False, "an instanced terrain shader would place the mesh's vertices from the heightmap");
            Assert.That(material.IsKeywordEnabled("_TERRAIN_INSTANCED_PERPIXEL_NORMAL"), Is.False);
            Assert.That(material.GetTexture("_Splat0"), Is.SameAs(texture));
            Assert.That((Vector2)material.GetVector("_Splat0_ST"), Is.EqualTo(Vector2.one));
            Assert.That(material.GetTexture("_Control"), Is.Not.Null);
            Assert.That(material.GetFloat("_Smoothness0"), Is.EqualTo(0.2f).Within(1e-4f));
            // the layer's colour scale (no property of a terrain shader: without it the splat draws black)
            Assert.That(material.GetVector("_DiffuseRemapScale0"), Is.EqualTo(Vector4.one));
        }

        // a terrain of more than four layers draws them four at a time: the mesh gets a pass for each four, the further ones
        // the shader's add pass on the next control map
        [Test] public void GroundMaterials_APassForEveryFourLayers()
        {
            var data = new TerrainData { heightmapResolution = 33, alphamapResolution = 32, size = new Vector3(500, 50, 400) };
            owned.Add(data);
            var layers = new TerrainLayer[6];
            for (int i = 0; i < layers.Length; i++)
            {
                var texture = new Texture2D(4, 4); owned.Add(texture);
                layers[i] = new TerrainLayer { diffuseTexture = texture, tileSize = new Vector2(10, 10) }; owned.Add(layers[i]);
            }
            data.terrainLayers = layers;
            var go = Terrain.CreateTerrainGameObject(data); owned.Add(go);
            var shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            Assume.That(shader, Is.Not.Null, "URP's terrain shader.");
            Assume.That(shader.GetDependency("AddPassShader"), Is.Not.Null, "URP's terrain add pass.");
            var template = new Material(shader); owned.Add(template);
            var passes = GeoTrenchGround.GroundMaterials(go.GetComponent<Terrain>(), template);
            foreach (var m in passes) owned.Add(m);
            Assert.That(passes.Length, Is.EqualTo(2));
            Assert.That(passes[1].shader, Is.SameAs(shader.GetDependency("AddPassShader")));
            Assert.That(passes[0].GetTexture("_Control"), Is.SameAs(data.alphamapTextures[0]));
            Assert.That(passes[1].GetTexture("_Control"), Is.SameAs(data.alphamapTextures[1]));
            Assert.That(passes[1].GetTexture("_Splat0"), Is.SameAs(layers[4].diffuseTexture));
            Assert.That(passes[1].GetTexture("_Splat1"), Is.SameAs(layers[5].diffuseTexture));
            // the mesh carries the ground's triangles again for the second pass
            var field = Field(); var uncut = Copy(field); var trenches = Trenches();
            GeoCombatGround.Carve(field, new List<CraterFeature>(), trenches, null, null, new List<string>());
            var chunks = GeoTrenchMesh.Build(trenches, uncut.Sample, field.Sample, new Rect(-32, -32, 64, 64), new Rect(-32, -32, 64, 64), 1f, 100, passes.Length);
            foreach (var c in chunks) owned.Add(c.mesh);
            Assert.That(chunks, Is.Not.Empty);
            foreach (var c in chunks)
            {
                Assert.That(c.mesh.subMeshCount, Is.EqualTo(3));
                Assert.That(c.mesh.GetTriangles(2), Is.EqualTo(c.mesh.GetTriangles(0)));
            }
        }
    }
}
