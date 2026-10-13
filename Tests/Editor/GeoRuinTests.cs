using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // Ruin models fitted to real footprints: planning the cuts along an axis, cutting a mesh, fitting it into a
    // building's frame and heights, choosing a model, and drawing it in editable and merged worlds. A small synthetic
    // model stands in for the imported ones, so the tests do not depend on ruins/models.
    public class GeoRuinTests
    {
        readonly List<Object> disposable = new List<Object>();
        T Keep<T>(T o) where T : Object { disposable.Add(o); return o; }

        [TearDown] public void Cleanup()
        {
            foreach (var o in disposable) if (o) Object.DestroyImmediate(o is Component c ? c.gameObject : o);
            disposable.Clear();
            GeoRuinFit.ClearCache();
        }

        // ------------------------------------------------------------------ a synthetic ruin

        // A 10 x 8 m house (walls to 3 m, a ridge 2 m above them along X) standing on a 26 x 20 m yard made of 1 m tiles.
        Mesh HouseMesh(float hx = 5, float hz = 4)
        {
            var shape = new GeoRuinFit.Shape();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                var n = Vector3.Cross(b - a, c - a).normalized;
                var t = new Vector4((b - a).normalized.x, (b - a).normalized.y, (b - a).normalized.z, 1);
                int i = shape.Add(a, n, t, new Vector2(0, 0)); shape.Add(b, n, t, new Vector2(1, 0)); shape.Add(c, n, t, new Vector2(1, 1)); shape.Add(d, n, t, new Vector2(0, 1));
                shape.triangles.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            // walls, faces outward (Unity winding: clockwise seen from outside)
            Vector3 P(float x, float y, float z) => new Vector3(x, y, z);
            Quad(P(-hx, 0, -hz), P(-hx, 3, -hz), P(hx, 3, -hz), P(hx, 0, -hz));
            Quad(P(hx, 0, hz), P(hx, 3, hz), P(-hx, 3, hz), P(-hx, 0, hz));
            Quad(P(-hx, 0, hz), P(-hx, 3, hz), P(-hx, 3, -hz), P(-hx, 0, -hz));
            Quad(P(hx, 0, -hz), P(hx, 3, -hz), P(hx, 3, hz), P(hx, 0, hz));
            // gable roof, ridge along X at 5 m
            Quad(P(-hx, 3, -hz), P(-hx, 5, 0), P(hx, 5, 0), P(hx, 3, -hz));
            Quad(P(hx, 3, hz), P(hx, 5, 0), P(-hx, 5, 0), P(-hx, 3, hz));
            // yard
            for (int x = -13; x < 13; x++)
                for (int z = -10; z < 10; z++)
                    Quad(P(x, 0.05f, z), P(x, 0.05f, z + 1), P(x + 1, 0.05f, z + 1), P(x + 1, 0.05f, z));
            return Keep(shape.ToMesh("Test ruin"));
        }

        GeoRuinModel Model(string id = "T01", string category = "house", GeoRuinAxis length = null)
        {
            var model = Keep(ScriptableObject.CreateInstance<GeoRuinModel>());
            model.id = id; model.mesh = HouseMesh();
            model.material = Keep(new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = "Ruin " + id });
            model.categories = new[] { category }; model.tags = new string[0];
            model.footprintMin = new Vector2(-5, -4); model.footprintMax = new Vector2(5, 4);
            model.eaves = 3; model.roofRise = 2; model.apron = 3;
            model.length = length ?? new GeoRuinAxis("CRRC", false, -3, 0, 3);
            return model;
        }

        GeoBuilding Building(string state, float length = 14, float width = 6, float turn = 30)
        {
            var building = Keep(new GameObject("Ruin test").AddComponent<GeoBuilding>());
            building.style = Keep(ScriptableObject.CreateInstance<GeoBuildingStyle>());
            building.sourceId = "osm/way/7"; building.category = "house";
            var rotation = Quaternion.Euler(0, turn, 0);
            building.footprint = new[] { new Vector3(-length / 2, 0, -width / 2), new Vector3(length / 2, 0, -width / 2), new Vector3(length / 2, 0, width / 2), new Vector3(-length / 2, 0, width / 2) }
                .Select(p => rotation * p).Select(p => new Vector2(p.x + 2, p.z - 1)).ToList();
            building.height = 6; building.roof = GeoRoofShape.Gabled; building.roofHeight = 2.5f;
            if (state != null) building.condition = new GeoBuildingCondition { state = state, basis = "reported", confidence = "medium" };
            return building;
        }

        static float Area(GeoRuinFit.Shape shape)
        {
            float area = 0;
            for (int i = 0; i < shape.triangles.Count; i += 3)
                area += Vector3.Cross(shape.positions[shape.triangles[i + 1]] - shape.positions[shape.triangles[i]], shape.positions[shape.triangles[i + 2]] - shape.positions[shape.triangles[i]]).magnitude * 0.5f;
            return area;
        }

        // ------------------------------------------------------------------ planning an axis

        [Test] public void Plan_KeepsTheModelWhole_WhenItAlreadyFits()
        {
            var plan = GeoRuinFit.Plan(new GeoRuinAxis("CRRC", false, -3, 0, 3), -5, 5, 10);
            Assert.That(plan.pieces.Count, Is.EqualTo(1), "Nothing is cut.");
            Assert.That(plan.length, Is.EqualTo(10).Within(0.0001f));
            Assert.That(plan.pieces[0].offset, Is.EqualTo(0).Within(0.0001f));
            var offCentre = GeoRuinFit.Plan(new GeoRuinAxis(), 2, 12, 10);
            Assert.That(offCentre.pieces[0].offset, Is.EqualTo(-7).Within(0.0001f), "The footprint is centred on 0.");
        }

        [Test] public void Plan_RepeatsBaysForALongerBuilding_AndLeavesThemOutForAShorterOne()
        {
            var axis = new GeoRuinAxis("CRFRC", false, -3, -1, 1, 3); // caps 2 m, bays 2 m, feature 2 m
            var longer = GeoRuinFit.Plan(axis, -5, 5, 16);
            Assert.That(longer.length, Is.EqualTo(16).Within(0.001f), "Three more bays.");
            Assert.That(longer.uses, Is.EqualTo(new[] { 1, 3, 1, 2, 1 }).Or.EqualTo(new[] { 1, 2, 1, 3, 1 }));
            Assert.That(longer.sequence.First(), Is.EqualTo(0)); Assert.That(longer.sequence.Last(), Is.EqualTo(4));
            var shorter = GeoRuinFit.Plan(axis, -5, 5, 6.2f);
            Assert.That(shorter.uses, Is.EqualTo(new[] { 1, 0, 1, 0, 1 }), "Both bays go; caps and the feature stay.");
            Assert.That(shorter.length, Is.EqualTo(6).Within(0.001f));
            var tiny = GeoRuinFit.Plan(axis, -5, 5, 3);
            Assert.That(tiny.uses, Is.EqualTo(new[] { 1, 0, 0, 0, 1 }), "Only a much smaller building loses the feature; caps never go.");
            // the pieces put the segments end to end around 0
            float end = float.NegativeInfinity;
            foreach (var piece in longer.pieces) { Assert.That(piece.from, Is.LessThan(piece.to)); end = piece.to + piece.offset; }
            Assert.That(longer.pieces[0].from, Is.EqualTo(float.NegativeInfinity)); Assert.That(longer.pieces.Last().to, Is.EqualTo(float.PositiveInfinity));
        }

        [Test] public void Plan_WithOpenEnds_CanUseASingleBox()
        {
            var row = new GeoRuinAxis("RRFFFRRR", true, -9.6f, -6.4f, -3.2f, 0, 3.2f, 6.4f, 9.6f); // eight 3.2 m boxes
            var shed = GeoRuinFit.Plan(row, -12.8f, 12.8f, 3.4f);
            Assert.That(shed.sequence.Count, Is.EqualTo(1), "One box for a small shed.");
            Assert.That(shed.roles[shed.sequence[0]], Is.EqualTo('F'), "The damaged box in the middle is the one kept.");
            Assert.That(shed.pieces.First().from, Is.EqualTo(float.NegativeInfinity), "The yard before the row comes along.");
            var closed = new GeoRuinAxis("CRFRC", false, -3, -1, 1, 3);
            Assert.That(GeoRuinFit.Plan(closed, -5, 5, 1).uses[0], Is.EqualTo(1), "A closed model keeps its end walls.");
        }

        [Test] public void Plan_TilesAModelWithoutBays_ForAMuchLongerBuilding()
        {
            var plan = GeoRuinFit.Plan(new GeoRuinAxis(), -5, 5, 31);
            Assert.That(plan.tiles, Is.EqualTo(3));
            Assert.That(plan.length, Is.EqualTo(30).Within(0.001f));
            Assert.That(plan.pieces.Count, Is.EqualTo(3), "Three copies, joined end to end.");
            Assert.That(GeoRuinFit.Plan(new GeoRuinAxis(), -5, 5, 13).tiles, Is.EqualTo(1), "A little longer is only stretched.");
        }

        [Test] public void Plan_IgnoresJointsThatDoNotMakeSense()
        {
            foreach (var axis in new[] { new GeoRuinAxis("CRC", false, 2, -2), new GeoRuinAxis("CR", false, -2, 2), new GeoRuinAxis("CXC", false, -2, 2), new GeoRuinAxis("CRC", false, -9, 2) })
            {
                Assert.IsFalse(GeoRuinFit.IsValid(axis, -5, 5));
                var plan = GeoRuinFit.Plan(axis, -5, 5, 20);
                Assert.That(plan.uses.Length, Is.EqualTo(1), "Only stretched.");
            }
        }

        // ------------------------------------------------------------------ cutting

        [Test] public void Cut_RepeatsAndLeavesOutPiecesWithoutLosingOrDoublingSurface()
        {
            var house = GeoRuinFit.Shape.Of(HouseMesh());
            float whole = Area(house);
            Assert.That(Area(GeoRuinFit.Cut(house, 0, GeoRuinFit.Plan(new GeoRuinAxis("CRRC", false, -3, 0, 3), -5, 5, 10).pieces)), Is.EqualTo(whole).Within(0.01f));
            // one more 3 m bay: the yard is cut too, so everything in the bay's slab is added once more
            var longer = GeoRuinFit.Cut(house, 0, GeoRuinFit.Plan(new GeoRuinAxis("CRRC", false, -3, 0, 3), -5, 5, 13).pieces);
            float slab = 3 * (2 * 3 + 2 * Mathf.Sqrt(4 * 4 + 2 * 2) + 20);
            Assert.That(Area(longer), Is.EqualTo(whole + slab).Within(0.05f));
            var bounds = new Bounds(longer.positions[0], Vector3.zero); foreach (var p in longer.positions) bounds.Encapsulate(p);
            Assert.That(bounds.size.x, Is.EqualTo(29).Within(0.001f), "26 m of yard and 3 m more house.");
            // two bays fewer
            var shorter = GeoRuinFit.Cut(house, 0, GeoRuinFit.Plan(new GeoRuinAxis("CRRC", false, -3, 0, 3), -5, 5, 4).pieces);
            Assert.That(Area(shorter), Is.EqualTo(whole - 2 * slab).Within(0.05f));
            foreach (int index in shorter.triangles) Assert.That(index, Is.InRange(0, shorter.positions.Count - 1));
        }

        [Test] public void Cut_TurnsEveryOtherWholeCopyEndForEnd_WithItsFacesStillOutward()
        {
            var house = GeoRuinFit.Shape.Of(HouseMesh());
            var plan = GeoRuinFit.Plan(new GeoRuinAxis(), -5, 5, 31);
            Assert.That(plan.pieces.Count(p => p.mirror), Is.EqualTo(1), "The middle one of three copies.");
            var row = GeoRuinFit.Cut(house, 0, plan.pieces);
            float slab = 2 * 10 * 3 + 2 * 8 * 3 + 2 * 10 * Mathf.Sqrt(4 * 4 + 2 * 2) + 10 * 20;
            Assert.That(Area(row), Is.EqualTo(Area(house) + 2 * slab).Within(0.1f));
            for (int i = 0; i < row.triangles.Count; i += 3)
            {
                var a = row.positions[row.triangles[i]]; var b = row.positions[row.triangles[i + 1]]; var c = row.positions[row.triangles[i + 2]];
                var face = Vector3.Cross(b - a, c - a);
                if (face.sqrMagnitude < 1e-8f) continue;
                Assert.That(Vector3.Dot(face.normalized, row.normals[row.triangles[i]]), Is.GreaterThan(0.99f));
            }
        }

        [Test] public void Build_RunsADeepModelsDepthAlongTheFootprint()
        {
            var model = Model(); model.mesh = HouseMesh(4, 5);
            model.footprintMin = new Vector2(-4, -5); model.footprintMax = new Vector2(4, 5); model.length = new GeoRuinAxis();
            Assert.IsTrue(model.LongAlongZ);
            var building = Building("destroyed", 14, 8, 0); building.ruinApron = 0;
            var target = GeoRuins.TargetOf(building, GeoPolygon.Clean(building.footprint));
            var mesh = Keep(GeoRuinFit.Build(model, target, out var fit));
            Assert.IsTrue(fit.swapped);
            Assert.That(fit.stretch.z, Is.EqualTo(1.4f).Within(0.001f), "10 m of depth along the 14 m side.");
            Assert.That(fit.stretch.x, Is.EqualTo(1).Within(0.001f));
            float along = 0, across = 0;
            foreach (var p in mesh.vertices.Where(p => p.y > 1))
            {
                var d = new Vector2(p.x, p.z) - target.frame.centre;
                along = Mathf.Max(along, Mathf.Abs(Vector2.Dot(d, target.frame.axis))); across = Mathf.Max(across, Mathf.Abs(Vector2.Dot(d, target.frame.Across)));
            }
            Assert.That(along, Is.EqualTo(7).Within(0.01f)); Assert.That(across, Is.EqualTo(4).Within(0.01f));
            Assert.That(GeoRuinFit.Measure(model, GeoRuins.TargetOf(Building("destroyed", 40, 8, 0), GeoPolygon.Clean(Building("destroyed", 40, 8, 0).footprint))).width.tiles,
                Is.EqualTo(4), "A deep model is repeated along the footprint, along its own depth.");
            var deep = Building("destroyed", 30, 14, 0);
            Assert.That(GeoRuinFit.Measure(Model(), GeoRuins.TargetOf(deep, GeoPolygon.Clean(deep.footprint))).width.tiles,
                Is.EqualTo(1), "14 m deep for an 8 m deep model: stretched, never one copy behind another.");
            var v = mesh.vertices; var n = mesh.normals; var t = mesh.triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                var face = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                if (face.sqrMagnitude < 1e-8f || (v[t[i]].y + v[t[i + 1]].y + v[t[i + 2]].y) / 3 < 0.5f) continue;
                Assert.That(Vector3.Dot(face.normalized, n[t[i]]), Is.GreaterThan(0.99f), "Turned, never mirrored.");
            }
        }

        [Test] public void Build_KeepsOrDropsLooseDebrisWhole()
        {
            // a 1 m chunk lying 1.6 m beyond the wall: half inside a 2 m apron
            var model = Model(); var shape = GeoRuinFit.Shape.Of(HouseMesh());
            int first = shape.positions.Count;
            foreach (var p in new[] { new Vector3(6.1f, 0, -0.5f), new Vector3(6.1f, 1.8f, 0), new Vector3(7.1f, 0, 0.5f) })
                shape.Add(p, Vector3.up, new Vector4(1, 0, 0, 1), Vector2.zero);
            shape.triangles.AddRange(new[] { first, first + 1, first + 2 });
            model.mesh = Keep(shape.ToMesh("House with a chunk"));
            var building = Building("destroyed", 10, 8, 0);
            var target = GeoRuins.TargetOf(building, GeoPolygon.Clean(building.footprint));
            target.apron = 2;
            // the chunk's top: the footprint is centred on (2, -1), walls rise to 3.5 m (x 7/6), so 1.8 m becomes 2.1
            bool Top(Vector3 p) => Mathf.Abs(p.x - 8.1f) < 0.05f && Mathf.Abs(p.z + 1) < 0.05f && Mathf.Abs(p.y - 2.1f) < 0.05f;
            var kept = Keep(GeoRuinFit.Build(model, target, out _));
            Assert.That(kept.vertices.Count(Top), Is.EqualTo(1), "Its middle lies inside the apron: it stays, top and all.");
            Assert.That(kept.vertices.Count(p => Mathf.Abs(p.x - 9.1f) < 0.05f && p.y < 0.5f && Mathf.Abs(p.z + 0.5f) < 0.05f), Is.EqualTo(1), "Its far corner too.");
            target.apron = 1; model.apron = 1;
            var dropped = Keep(GeoRuinFit.Build(model, target, out _));
            Assert.That(dropped.vertices.Count(Top), Is.EqualTo(0), "Its middle lies beyond: all of it goes, no floating top.");
        }

        [Test] public void Cut_ClipsTrianglesAtThePlaneAndInterpolatesTheirData()
        {
            var shape = new GeoRuinFit.Shape();
            shape.Add(new Vector3(-2, 0, 0), Vector3.up, new Vector4(1, 0, 0, 1), new Vector2(0, 0));
            shape.Add(new Vector3(2, 0, 1), Vector3.up, new Vector4(1, 0, 0, 1), new Vector2(1, 0));
            shape.Add(new Vector3(2, 0, -1), Vector3.up, new Vector4(1, 0, 0, 1), new Vector2(1, 1));
            shape.triangles.AddRange(new[] { 0, 1, 2 });
            var right = GeoRuinFit.Cut(shape, 0, new List<GeoRuinFit.Piece> { new GeoRuinFit.Piece { from = 0, to = float.PositiveInfinity, offset = 5 } });
            Assert.That(Area(right), Is.EqualTo(Area(shape) * 0.75f).Within(0.0001f), "The part right of the plane.");
            Assert.That(right.positions.Min(p => p.x), Is.EqualTo(5).Within(0.0001f), "Moved by the piece's offset.");
            foreach (var uv in right.uvs) Assert.That(uv.x, Is.InRange(0.5f - 0.0001f, 1.0001f), "Texture coordinates follow the clip.");
        }

        // ------------------------------------------------------------------ the footprint's frame

        [Test] public void Frame_FindsTheTurnedRectangleAndHowFullItIs()
        {
            var rotation = Quaternion.Euler(0, 35, 0);
            var outline = new[] { new Vector3(-7, 0, -3), new Vector3(7, 0, -3), new Vector3(7, 0, 3), new Vector3(-7, 0, 3) }.Select(p => rotation * p).Select(p => new Vector2(p.x + 10, p.z)).ToList();
            var frame = GeoRuinFit.FrameOf(outline);
            Assert.That(frame.length, Is.EqualTo(14).Within(0.001f)); Assert.That(frame.width, Is.EqualTo(6).Within(0.001f));
            Assert.That(frame.centre.x, Is.EqualTo(10).Within(0.001f)); Assert.That(frame.fill, Is.EqualTo(1).Within(0.001f));
            var along = rotation * Vector3.right;
            Assert.That(Mathf.Abs(Vector2.Dot(frame.axis, new Vector2(along.x, along.z))), Is.EqualTo(1).Within(0.0001f));
            var l = new List<Vector2> { new Vector2(0, 0), new Vector2(8, 0), new Vector2(8, 3), new Vector2(3, 3), new Vector2(3, 8), new Vector2(0, 8) };
            Assert.That(GeoRuinFit.FrameOf(l).fill, Is.LessThan(0.7f), "An L fills little of its box.");
        }

        // ------------------------------------------------------------------ fitting

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void Build_FitsTheModelToTheFootprintAndTheHeights(bool mirror, bool turn)
        {
            var model = Model(); var building = Building("destroyed");
            building.ruinMirror = mirror; building.ruinTurn = turn; building.ruinApron = 0;
            var outline = GeoPolygon.Clean(building.footprint);
            var target = GeoRuins.TargetOf(building, outline);
            var mesh = Keep(GeoRuinFit.Build(model, target, out var fit));
            Assert.That(fit.length.Added, Is.EqualTo(1), "A 14 m house is the 10 m model with one more 3 m bay.");
            Assert.That(fit.stretch.x, Is.EqualTo(14f / 13f).Within(0.001f));
            Assert.That(fit.stretch.z, Is.EqualTo(0.75f).Within(0.001f), "8 m deep made 6.");
            Assert.That(fit.stretch.y, Is.EqualTo(3.5f / 3f).Within(0.001f), "Walls to the eaves (6 m less a 2.5 m roof).");
            Assert.That(fit.roofStretch, Is.EqualTo(1.25f).Within(0.001f), "The roof to the building's rise.");
            var v = mesh.vertices; var n = mesh.normals; var t = mesh.triangles;
            // in the footprint's own frame, the walls fill the footprint
            float maxAlong = 0, maxAcross = 0, top = 0;
            foreach (var p in v)
            {
                var d = new Vector2(p.x, p.z) - target.frame.centre;
                if (p.y > 1) { maxAlong = Mathf.Max(maxAlong, Mathf.Abs(Vector2.Dot(d, target.frame.axis))); maxAcross = Mathf.Max(maxAcross, Mathf.Abs(Vector2.Dot(d, target.frame.Across))); }
                top = Mathf.Max(top, p.y);
                Assert.IsTrue(float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z));
            }
            Assert.That(maxAlong, Is.EqualTo(7).Within(0.01f)); Assert.That(maxAcross, Is.EqualTo(3).Within(0.01f));
            Assert.That(top, Is.EqualTo(6).Within(0.01f), "Ridge at the building's full height.");
            // walls and roof still face the way their normals do (mirroring turns the triangles round); the yard is left
            // out, as its trimmed edge sinks a little into the ground
            for (int i = 0; i < t.Length; i += 3)
            {
                var face = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                if (face.sqrMagnitude < 1e-8f || (v[t[i]].y + v[t[i + 1]].y + v[t[i + 2]].y) / 3 < 0.5f) continue;
                Assert.That(Vector3.Dot(face.normalized, n[t[i]]), Is.GreaterThan(0.99f));
            }
            foreach (var normal in n) Assert.That(normal.magnitude, Is.EqualTo(1).Within(0.001f));
        }

        [Test] public void Build_LeavesOutYardFarFromTheFootprint_AndLaysTheRestOnTheGround()
        {
            var model = Model(); var building = Building("destroyed", 10, 8, 0);
            building.ruinApron = 2;
            var outline = GeoPolygon.Clean(building.footprint);
            var target = GeoRuins.TargetOf(building, outline);
            var flat = Keep(GeoRuinFit.Build(model, target, out _));
            foreach (var p in flat.vertices)
            {
                var d = new Vector2(p.x, p.z) - target.frame.centre;
                Assert.That(Mathf.Abs(d.x), Is.LessThanOrEqualTo(5 + 2 + 1.5f), "Yard tiles further than the apron are gone.");
            }
            target.ground = p => -0.5f;
            var sloped = Keep(GeoRuinFit.Build(model, target, out _));
            Assert.That(sloped.vertices.Where(p => p.y < 0.5f).Max(p => p.y), Is.LessThan(-0.3f), "Ground-level parts follow the terrain down.");
            Assert.That(sloped.bounds.max.y, Is.EqualTo(flat.bounds.max.y).Within(0.001f), "The roof stays where it was.");
        }

        [Test] public void Build_IsTheSameEveryTime()
        {
            var model = Model(); var building = Building("destroyed");
            var target = GeoRuins.TargetOf(building, GeoPolygon.Clean(building.footprint));
            var first = Keep(GeoRuinFit.Build(model, target, out _)).vertices;
            var second = Keep(GeoRuinFit.Build(model, target, out _)).vertices;
            Assert.That(second, Is.EqualTo(first));
        }

        // ------------------------------------------------------------------ choosing and drawing

        GeoRuinLibrary Library(params GeoRuinModel[] models)
        {
            var library = Keep(ScriptableObject.CreateInstance<GeoRuinLibrary>());
            library.models = models.ToList();
            return library;
        }

        [Test] public void Library_PicksByConditionCategoryAndTag()
        {
            var house = Model("H"); var heavy = Model("X"); heavy.minState = GeoConditionState.HeavilyDamaged;
            var church = Model("C", "religious"); church.tags = new[] { "church" }; church.autoSelect = false;
            var library = Library(house, heavy, church);
            Assert.IsNull(library.Choose(Building(null), GeoRuinUse.AllDamage, out var reason), reason);
            Assert.IsNull(library.Choose(Building("intact"), GeoRuinUse.AllDamage, out _));
            Assert.That(library.Choose(Building("damaged"), GeoRuinUse.AllDamage, out _), Is.SameAs(house), "Only one model shows light damage.");
            Assert.IsNull(library.Choose(Building("damaged"), GeoRuinUse.HeavilyDamagedAndDestroyed, out _));
            Assert.IsNull(library.Choose(Building("destroyed"), GeoRuinUse.Off, out _));
            var shed = Building("destroyed"); shed.category = "shed";
            Assert.IsNull(library.Choose(shed, GeoRuinUse.AllDamage, out _), "No model stands for a shed.");
            var chapel = Building("destroyed"); chapel.category = "religious";
            Assert.IsNull(library.Choose(chapel, GeoRuinUse.AllDamage, out _), "A one-of-a-kind model needs its tag.");
            chapel.buildingTag = "church";
            Assert.That(library.Choose(chapel, GeoRuinUse.AllDamage, out _), Is.SameAs(church));
            var l = Building("destroyed"); l.footprint = new List<Vector2> { new Vector2(0, 0), new Vector2(12, 0), new Vector2(12, 3), new Vector2(3, 3), new Vector2(3, 12), new Vector2(0, 12) };
            Assert.IsNull(library.Choose(l, GeoRuinUse.AllDamage, out reason), "An L-shaped footprint keeps procedural damage.");
            // the pick is the same every time, and differs between buildings among near equals
            var picks = new HashSet<GeoRuinModel>();
            for (int i = 0; i < 40; i++)
            {
                var b = Building("destroyed"); b.sourceId = "osm/way/" + (100 + i);
                var pick = library.Choose(b, GeoRuinUse.AllDamage, out _);
                Assert.That(library.Choose(b, GeoRuinUse.AllDamage, out _), Is.SameAs(pick));
                picks.Add(pick);
            }
            Assert.That(picks, Is.EquivalentTo(new[] { house, heavy }), "Equal models share the destroyed houses.");
        }

        [Test] public void Assign_FollowsTheBuildingsRuinMode()
        {
            var model = Model(); var other = Model("O");
            var settings = new GenerationSettings { ruinLibrary = Library(model), ruinApron = 5 };
            var building = Building("destroyed");
            GeoRuins.Assign(building, settings);
            Assert.That(building.ruin, Is.SameAs(model)); Assert.That(building.ruinApron, Is.EqualTo(5));
            building.ruinMode = GeoRuinMode.Model; building.ruin = other;
            GeoRuins.Assign(building, settings);
            Assert.That(building.ruin, Is.SameAs(other), "A model chosen by hand stays.");
            building.ruinMode = GeoRuinMode.Procedural;
            GeoRuins.Assign(building, settings);
            Assert.IsNull(building.ruin);
            building.ruinMode = GeoRuinMode.Auto; building.ruin = other;
            GeoRuins.Assign(building, null);
            Assert.That(building.ruin, Is.SameAs(other), "Without settings nothing changes.");
        }

        [Test] public void Building_WithARuin_IsDrawnAsTheModelAlone()
        {
            var building = Building("destroyed"); building.ruin = Model();
            using (var output = GeoBuildingGeometry.Build(building, 0.5f))
            {
                Assert.That(output.parts.Select(p => p.name), Is.EqualTo(new[] { "Ruin" }));
                Assert.That(output.parts[0].material, Is.SameAs(building.ruin.material));
                Assert.IsTrue(output.parts[0].collider);
                Assert.That(output.parts[0].mesh.tangents.Length, Is.EqualTo(output.parts[0].mesh.vertexCount), "Tangents for the normal map.");
            }
            building.ruinMode = GeoRuinMode.Procedural;
            using (var output = GeoBuildingGeometry.Build(building, 0.5f))
                Assert.That(output.parts.Select(p => p.name), Does.Contain("Rubble").And.Not.Contain("Ruin"), "Procedural damage again.");
        }

        [Test] public void EditableBuilding_IsCreatedWithItsRuinModelAndKeepsAHandPickedOne()
        {
            var model = Model(); var other = Model("O");
            var parent = Keep(new GameObject("Editable ruins test")).transform;
            var source = new BuildingFeature
            {
                id = "osm/way/9", category = "house", levels = 1, x = 50, z = -20, width = 8, length = 10, rotation = 30, height = 5, buildingTag = "house",
                footprint = new[] { -4f, -5f, 4f, -5f, 4f, 5f, -4f, 5f },
                condition = new GeoBuildingCondition { state = "destroyed", basis = "reported", confidence = "medium" }
            };
            var settings = new GenerationSettings { ruinLibrary = Library(model) };
            var building = GeoBuildingBuilder.Create(source, parent, p => 3f, o => disposable.Add(o), new List<string>(), null, settings);
            Assert.That(building.ruin, Is.SameAs(model)); Assert.That(building.ruinMode, Is.EqualTo(GeoRuinMode.Auto));
            var part = building.generatedGeometry.Find("Ruin");
            Assert.IsNotNull(part, "The generated geometry is the ruin.");
            Assert.That(part.GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(model.material), "The model's own material, not a tinted copy.");
            Assert.IsNotNull(part.GetComponent<MeshCollider>());
            Assert.That(building.transform.position.y, Is.EqualTo(3f).Within(0.001f));
            // a model chosen by hand survives a rebuild from the export
            building.ruinMode = GeoRuinMode.Model; building.ruin = other;
            var rebuilt = GeoBuildingBuilder.Create(source, parent, p => 3f, o => disposable.Add(o), new List<string>(), building, settings);
            Assert.That(rebuilt.ruin, Is.SameAs(other));
            Assert.That(rebuilt.generatedGeometry.Find("Ruin").GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(other.material));
        }

        [Test] public void MergedCity_GivesEachRuinModelItsOwnSubMesh()
        {
            var model = Model();
            var group = Keep(new GameObject("Merged ruins test")).transform;
            BuildingFeature House(string id, float x, string state) => new BuildingFeature
            {
                id = id, category = "house", levels = 1, x = x, z = 0, width = 8, length = 10, rotation = 20, height = 5, buildingTag = "house",
                footprint = new[] { -4f, -5f, 4f, -5f, 4f, 5f, -4f, 5f },
                condition = state == null ? null : new GeoBuildingCondition { state = state, basis = "reported", confidence = "medium" }
            };
            var sources = new List<BuildingFeature> { House("whole", 0, null), House("ruin", 30, "destroyed"), House("ruin2", 60, "heavily_damaged") };
            var settings = new GenerationSettings { chunkSize = 1000, ruinLibrary = Library(model) };
            var notes = new List<string>();
            GeoCityBuilder.Build(sources, new GeoBounds { minX = -500, minZ = -500, maxX = 500, maxZ = 500 }, group, p => 1f, o => disposable.Add(o), notes, settings, null, 3);
            var chunk = group.GetComponentInChildren<GeoCityChunk>();
            var mesh = chunk.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(mesh.subMeshCount, Is.EqualTo(3), "Palette, glass and the ruin model.");
            Assert.That(chunk.GetComponent<MeshRenderer>().sharedMaterials[2], Is.SameAs(model.material));
            Assert.That(mesh.tangents.Length, Is.EqualTo(mesh.vertexCount));
            Assert.That(chunk.buildings.Single(e => e.id == "ruin").ruin, Is.EqualTo("T01"));
            Assert.That(chunk.buildings.Single(e => e.id == "whole").ruin, Is.Null.Or.Empty);
            Assert.That(notes.Any(n => n.Contains("2 damaged buildings are drawn with ruin models") && n.Contains("T01 x 2")), string.Join("\n", notes));
            var ruinTriangles = mesh.GetTriangles(2);
            var vertices = mesh.vertices;
            Assert.That(ruinTriangles.Select(i => vertices[i].x).Min(), Is.GreaterThan(15), "The whole house is not a ruin.");
        }

        // ------------------------------------------------------------------ importing

        [Test] public void Glb_IsReadInUnityAxes()
        {
            // one triangle, counter-clockwise in glTF, with UVs
            float[] positions = { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, normals = { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, uvs = { 0, 0, 1, 0, 0, 1 };
            ushort[] indices = { 0, 1, 2 };
            var binary = new List<byte>();
            foreach (var f in positions.Concat(normals).Concat(uvs)) binary.AddRange(BitConverter.GetBytes(f));
            foreach (var i in indices) binary.AddRange(BitConverter.GetBytes(i));
            while (binary.Count % 4 != 0) binary.Add(0);
            string json = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":" + binary.Count + "}]," +
                "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":36,\"byteLength\":36},{\"buffer\":0,\"byteOffset\":72,\"byteLength\":24},{\"buffer\":0,\"byteOffset\":96,\"byteLength\":6}]," +
                "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},{\"bufferView\":2,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\"},{\"bufferView\":3,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}]," +
                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3}]}]}";
            var text = new List<byte>(System.Text.Encoding.UTF8.GetBytes(json));
            while (text.Count % 4 != 0) text.Add(0x20);
            var file = new List<byte>();
            file.AddRange(BitConverter.GetBytes(0x46546C67u)); file.AddRange(BitConverter.GetBytes(2u)); file.AddRange(BitConverter.GetBytes((uint)(12 + 8 + text.Count + 8 + binary.Count)));
            file.AddRange(BitConverter.GetBytes((uint)text.Count)); file.AddRange(BitConverter.GetBytes(0x4E4F534Au)); file.AddRange(text);
            file.AddRange(BitConverter.GetBytes((uint)binary.Count)); file.AddRange(BitConverter.GetBytes(0x004E4942u)); file.AddRange(binary);
            string path = Path.Combine(Path.GetTempPath(), "geoworld-ruin-test.glb");
            File.WriteAllBytes(path, file.ToArray());
            try
            {
                var mesh = Keep(GeoRuinImporter.ReadGlb(path));
                Assert.That(mesh.vertices, Is.EqualTo(new[] { Vector3.zero, new Vector3(-1, 0, 0), new Vector3(0, 1, 0) }), "X is mirrored.");
                Assert.That(mesh.normals[0], Is.EqualTo(new Vector3(0, 0, 1)));
                Assert.That(mesh.uv, Is.EqualTo(new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 0) }), "V runs up.");
                Assert.That(mesh.triangles, Is.EqualTo(new[] { 0, 2, 1 }), "Triangles turned round.");
                var v = mesh.vertices;
                Assert.That(Vector3.Dot(Vector3.Cross(v[2] - v[0], v[1] - v[0]), mesh.normals[0]), Is.GreaterThan(0), "Unity's clockwise front face points along the normal.");
            }
            finally { File.Delete(path); }
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            try { Assert.Throws<InvalidDataException>(() => GeoRuinImporter.ReadGlb(path)); }
            finally { File.Delete(path); }
        }
    }
}
