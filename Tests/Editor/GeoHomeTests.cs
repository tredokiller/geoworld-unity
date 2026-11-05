using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // Intact-building models on buildings: roof shape names, roof colours, choosing a model, fitting it to a footprint and
    // giving it the roof of the export. A small synthetic model stands in for the imported ones.
    public class GeoHomeTests
    {
        readonly List<Object> disposable = new List<Object>();
        T Keep<T>(T o) where T : Object { disposable.Add(o); return o; }
        // the editor's hook (GeoHomeImporter sets it when scripts load): a test may replace it, the editor gets it back
        System.Func<Material, Color, Material> editorRoofs;

        [SetUp] public void Setup() => editorRoofs = GeoHomes.RoofMaterialProvider;

        [TearDown] public void Cleanup()
        {
            foreach (var o in disposable) if (o) Object.DestroyImmediate(o is Component c ? c.gameObject : o);
            disposable.Clear();
            GeoHomes.RoofMaterialProvider = editorRoofs;
        }

        // A 10 x 7 m body (walls to 2.8 m, front towards +Z) with a ridge roof variant 2.2 m above the eaves.
        Mesh Box(float x0, float x1, float y0, float y1, float z0, float z1, int sub)
        {
            var mesh = Keep(new Mesh());
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                int i = v.Count; v.AddRange(new[] { a, b, c, d }); n.AddRange(new[] { normal, normal, normal, normal });
                uv.AddRange(new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up }); t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            Quad(new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), Vector3.forward);
            Quad(new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), Vector3.back);
            Quad(new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), Vector3.right);
            Quad(new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), Vector3.left);
            Quad(new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0), Vector3.up);
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(sub == 0 ? t : new List<int>(), 0); mesh.SetTriangles(sub == 1 ? t : new List<int>(), 1);
            mesh.RecalculateTangents(); mesh.RecalculateBounds();
            return mesh;
        }

        GeoHomeModelAsset Model(string id = "T01", string category = "house", string tag = null, bool tagOnly = false, float length = 10, float width = 7)
        {
            var model = Keep(ScriptableObject.CreateInstance<GeoHomeModelAsset>());
            model.id = id; model.categories = new[] { category }; model.tags = tag != null ? new[] { tag } : new string[0]; model.tagOnly = tagOnly;
            model.length = length; model.width = width; model.eaveHeight = 2.8f; model.rise = 2.2f; model.floors = 1;
            model.body = Box(-length / 2, length / 2, 0, 2.8f, -width / 2, width / 2, 0);
            model.variantNames = new[] { "gabled_along", "flat" };
            model.variants = new[] { Box(-length / 2, length / 2, 2.8f, 5.0f, -width / 2, width / 2, 1), Box(-length / 2, length / 2, 2.8f, 3.2f, -width / 2, width / 2, 1) };
            model.material = Keep(new Material(Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader")));
            return model;
        }

        GeoBuilding Building(float length = 10, float width = 7, float turn = 25, string state = null, string category = "house")
        {
            var building = Keep(new GameObject("Home test").AddComponent<GeoBuilding>());
            building.style = Keep(ScriptableObject.CreateInstance<GeoBuildingStyle>());
            building.sourceId = "osm/way/42"; building.category = category;
            var rotation = Quaternion.Euler(0, turn, 0);
            building.footprint = new[] { new Vector3(-length / 2, 0, -width / 2), new Vector3(length / 2, 0, -width / 2), new Vector3(length / 2, 0, width / 2), new Vector3(-length / 2, 0, width / 2) }
                .Select(p => rotation * p).Select(p => new Vector2(p.x + 3, p.z - 2)).ToList();
            building.height = 5; building.roof = GeoRoofShape.Gabled; building.roofHeight = 2.2f;
            if (state != null) building.condition = new GeoBuildingCondition { state = state, basis = "reported", confidence = "medium" };
            return building;
        }

        GeoHomeLibrary Library(params GeoHomeModelAsset[] models)
        {
            var library = Keep(ScriptableObject.CreateInstance<GeoHomeLibrary>());
            library.models = models.ToList();
            return library;
        }

        // ------------------------------------------------------------------ roof names and colours

        [TestCase("gabled", "along", "gabled_along")]
        [TestCase("gabled", "across", "gabled_across")]
        [TestCase("gable", null, "gabled_along")]
        [TestCase("hipped", null, "hipped")]
        [TestCase("pyramidal", null, "hipped")]
        [TestCase("half-hipped", null, "half_hipped")]
        [TestCase("skillion", null, "skillion")]
        [TestCase("flat", null, "flat")]
        public void RoofShapesOfTheExportPickTheirVariant(string shape, string ridge, string expected)
        {
            Assert.AreEqual(expected, GeoHomeModel.VariantFor(shape, null, ridge, "fallback"));
        }

        [Test] public void AnUnknownShapeFallsBackToRenderAsThenTheModelDefault()
        {
            Assert.AreEqual("hipped", GeoHomeModel.VariantFor("onion", "hipped", null, "gabled_along"));
            Assert.AreEqual("gabled_along", GeoHomeModel.VariantFor("onion", null, null, "gabled_along"));
            Assert.AreEqual("gabled_along", GeoHomeModel.VariantFor(null, null, null, "gabled_along"));
        }

        [TestCase("metal", "metal_tile")]
        [TestCase("asbestos_cement", "asbestos_cement")]
        [TestCase("clay_tiles", "clay_tiles")]
        [TestCase("bitumen", "bitumen")]
        [TestCase("Slate", "slate")]
        public void RoofMaterialsOfTheExportPickTheirTexture(string material, string kind)
        {
            Assert.AreEqual(kind, GeoRoofMaterialSet.KindFor(material));
        }

        [Test] public void RoofColoursComeFromASmallPalette()
        {
            var c = GeoHomes.Quantize(new Color(0.43f, 0.27f, 0.21f));
            Assert.AreEqual(GeoHomes.Quantize(c), c, "quantising twice changes nothing");
            Assert.AreEqual(GeoHomes.Quantize(new Color(0.44f, 0.28f, 0.2f)), c, "near colours share a material");
            Assert.AreNotEqual(GeoHomes.Quantize(new Color(0.9f, 0.2f, 0.2f)), c);
        }

        // ------------------------------------------------------------------ choosing

        [Test] public void AHouseGetsAModelOfItsKindAndSize()
        {
            var building = Building();
            var small = Model("T01", length: 6, width: 5); var fitting = Model("T02"); var shop = Model("T03", "commercial");
            var chosen = Library(small, fitting, shop).Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.IntactAndUnknown);
            Assert.AreEqual("T02", chosen.id, "the house model that needs the least stretching");
        }

        [Test] public void NothingIsChosenWhenOffDamagedTooDifferentOrNotARectangle()
        {
            var library = Library(Model());
            var building = Building();
            Assert.IsNull(library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.Off));
            var damaged = Building(state: "damaged");
            Assert.IsNull(library.Choose(damaged, GeoPolygon.Clean(damaged.footprint), GeoHomeUse.IntactAndUnknown), "damaged buildings keep ruins and procedural damage");
            var big = Building(length: 30, width: 20);
            Assert.IsNull(library.Choose(big, GeoPolygon.Clean(big.footprint), GeoHomeUse.IntactAndUnknown), "stretch limit");
            var l = Building();
            l.footprint = new List<Vector2> { new Vector2(0, 0), new Vector2(12, 0), new Vector2(12, 3), new Vector2(3, 3), new Vector2(3, 12), new Vector2(0, 12) };
            Assert.IsNull(library.Choose(l, GeoPolygon.Clean(l.footprint), GeoHomeUse.IntactAndUnknown), "an L is not a rectangle");
        }

        [Test] public void AModelWithTheBuildingsFloorCountWinsAtTheSameSize()
        {
            var hall = Model("T01", "industrial", length: 10, width: 7); hall.floors = 1;
            var office = Model("T02", "industrial", length: 10, width: 7); office.floors = 2;
            var library = Library(hall, office);
            var building = Building(category: "industrial");
            building.floors = 2;
            Assert.AreEqual("T02", library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.IntactAndUnknown).id);
            building.floors = 1;
            Assert.AreEqual("T01", library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.IntactAndUnknown).id);
        }

        [Test] public void AnEstimatedLowHeightKeepsTheModelsLowestWallsAndAMappedOneMustFit()
        {
            var model = Model();   // walls 2.8 m
            var library = Library(model);
            var building = Building(turn: 0);
            building.height = 3; building.roof = GeoRoofShape.Gabled; building.roofHeight = 1.2f;   // walls 1.8 m: 0.64 of the model's
            building.heightSource = "levels";
            Assert.AreEqual(model, library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.IntactAndUnknown), "an estimated height may be low");
            building.heightSource = "tag";
            Assert.IsNull(library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.IntactAndUnknown), "a mapped height must fit");

            building.heightSource = "levels"; building.home = model; building.homeLibrary = library;
            building.roofInfo = new GeoRoofInfo { shape = "gabled", ridge = "along", rise = 1.2f };
            using (var output = GeoHomes.Build(new GeoBuildingContext(building), 0.2f))
            {
                var body = output.parts.First(p => p.material == model.material).mesh;
                Assert.AreEqual(2.8f * GeoHomeLibrary.Squash, body.bounds.max.y, 0.05f, "the walls stay at the model's lowest");
                var roof = output.parts.Where(p => p.material == model.material).Skip(1).Select(p => p.mesh).FirstOrDefault()
                           ?? output.parts.First(p => p.material != model.material).mesh;
                Assert.GreaterOrEqual(roof.bounds.min.y, 2.8f * GeoHomeLibrary.Squash - 0.05f, "the roof sits on the walls, not through them");
            }
        }

        [Test] public void AllStandingAlsoDrawsDamagedButNotHeavilyDamagedOrDestroyed()
        {
            var library = Library(Model());
            foreach (var state in new[] { null, "intact", "damaged" })
            {
                var building = Building(state: state);
                Assert.IsNotNull(library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.AllStanding), state ?? "unknown");
            }
            foreach (var state in new[] { "heavily_damaged", "destroyed" })
            {
                var building = Building(state: state);
                Assert.IsNull(library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.AllStanding), state + " is a ruin");
            }
        }

        [Test] public void AModelForATagIsOnlyUsedOnThatTag()
        {
            var school = Model("T09", "public", "school", true, 10, 7);
            var library = Library(school);
            var plain = Building(category: "public");
            Assert.IsNull(library.Choose(plain, GeoPolygon.Clean(plain.footprint), GeoHomeUse.IntactAndUnknown));
            var tagged = Building(category: "public"); tagged.buildingTag = "school";
            Assert.AreEqual("T09", library.Choose(tagged, GeoPolygon.Clean(tagged.footprint), GeoHomeUse.IntactAndUnknown).id);
        }

        [Test] public void TheChoiceOnlyDependsOnTheBuildingsId()
        {
            var library = Library(Model("T01"), Model("T02"), Model("T03"));
            var building = Building();
            var first = library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.IntactAndUnknown);
            for (int i = 0; i < 3; i++) Assert.AreSame(first, library.Choose(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.IntactAndUnknown));
        }

        // ------------------------------------------------------------------ fitting and roofs

        [Test] public void AModelIsStretchedToTheFootprintAndItsRoofToTheRise()
        {
            var model = Model();
            var building = Building(length: 12, width: 8, turn: 0);
            building.home = model; building.homeMode = GeoRuinMode.Auto; building.homeLibrary = Library(model);
            building.roofInfo = new GeoRoofInfo { shape = "gabled", ridge = "along", rise = 1.6f };
            building.height = 4.4f; building.roofHeight = 1.6f;
            var context = new GeoBuildingContext(building);
            using (var output = GeoHomes.Build(context, 0.5f))
            {
                Assert.GreaterOrEqual(output.parts.Count(p => p.material == model.material), 1, "a mesh with the body atlas");
                var body = output.parts.First(p => p.material == model.material).mesh;
                Assert.AreEqual(12f, body.bounds.size.x, 0.05f);
                Assert.AreEqual(8f, body.bounds.size.z, 0.05f);
                Assert.AreEqual(2.8f, body.bounds.max.y, 0.05f, "walls to the eaves (4.4 - 1.6)");
                Assert.AreEqual(-0.5f, body.bounds.min.y, 0.01f, "the foot reaches down to the foundation");
            }
        }

        [Test] public void TheRoofOfTheExportDecidesVariantRiseAndTint()
        {
            var model = Model();
            var kind = Keep(new Material(Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader"))); kind.name = "Roof_metal_tile";
            var set = Keep(ScriptableObject.CreateInstance<GeoRoofMaterialSet>());
            set.entries = new[] { new GeoRoofMaterialSet.Entry { kind = "metal_tile", material = kind } };
            var library = Library(model); library.roofMaterials = set;
            Color asked = default;
            GeoHomes.RoofMaterialProvider = (m, c) => { asked = c; return m; };
            var building = Building(turn: 0);
            building.home = model; building.homeLibrary = library; building.height = 5; building.roofHeight = 2.2f;
            building.roofInfo = new GeoRoofInfo { shape = "flat", material = "metal", color = "#6e4636", colorSource = "estimated" };
            using (var output = GeoHomes.Build(new GeoBuildingContext(building), 0.2f))
            {
                var roof = output.parts.First(p => p.material == kind);
                Assert.AreEqual(GeoHomes.Quantize(new Color(0x6e / 255f, 0x46 / 255f, 0x36 / 255f)), asked);
                Assert.Less(roof.mesh.bounds.size.y, 1.0f, "the flat variant is low");
            }
        }

        [Test] public void MirroringAndTurningKeepTheFootprintAndTheWindingOutward()
        {
            var model = Model();
            foreach (var (turn, mirror) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var building = Building(turn: 40);
                building.home = model; building.homeLibrary = Library(model); building.homeTurn = turn; building.homeMirror = mirror;
                using (var output = GeoHomes.Build(new GeoBuildingContext(building), 0.3f))
                {
                    var mesh = output.parts.First(p => p.material == model.material).mesh;
                    var vertices = mesh.vertices; var triangles = mesh.triangles; var normals = mesh.normals;
                    var centre = mesh.bounds.center;
                    int outward = 0, total = 0;
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        var a = vertices[triangles[i]]; var b = vertices[triangles[i + 1]]; var c = vertices[triangles[i + 2]];
                        var face = Vector3.Cross(b - a, c - a).normalized;   // Unity: clockwise seen from outside, so this points outward
                        if (Vector3.Dot(face, (a + b + c) / 3 - centre) > 0) outward++;
                        total++;
                        Assert.Greater(Vector3.Dot(face, normals[triangles[i]]), 0.5f, "vertex normals agree with the winding");
                    }
                    Assert.AreEqual(total, outward, "turn " + turn + " mirror " + mirror);
                }
            }
        }

        // ------------------------------------------------------------------ plans: what a model covers from above

        // Several boxes (x0, x1, y0, y1, z0, z1) as one mesh on one sub-mesh, like a body with its porch or wing.
        Mesh Blocks(int sub, params float[][] boxes)
        {
            var parts = boxes.Select(b => Box(b[0], b[1], b[2], b[3], b[4], b[5], sub)).ToArray();
            var mesh = Keep(new Mesh());
            var combine = parts.Select(p => new CombineInstance { mesh = p, subMeshIndex = sub, transform = Matrix4x4.identity }).ToArray();
            mesh.CombineMeshes(combine, true, false);
            // the combined triangles back on the sub-mesh the model asks for
            var triangles = mesh.triangles;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(sub == 0 ? triangles : new int[0], 0); mesh.SetTriangles(sub == 1 ? triangles : new int[0], 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        // A 10 x 7 m house (walls to 2.8 m) and what it has beyond its walls: a porch, a wing.
        GeoHomeModelAsset Extended(string id, params float[][] extra)
        {
            var model = Model(id);
            var blocks = new List<float[]> { new[] { -5f, 5f, 0, 2.8f, -3.5f, 3.5f } };
            blocks.AddRange(extra);
            model.body = Blocks(0, blocks.ToArray());
            return model;
        }

        static bool Within(IList<Vector2> outline, Vector2 p, float tolerance)
        {
            if (GeoPolygon.Contains(outline, p)) return true;
            for (int i = 0; i < outline.Count; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % outline.Count], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
                if (Vector2.Distance(p, a + ab * t) <= tolerance) return true;
            }
            return false;
        }

        // every vertex of the baked model (the building's local space) in the footprint, a few centimetres either way
        void AssertWithinFootprint(GeoBuilding building, GeoHomeModelAsset model, string message)
        {
            var outline = GeoPolygon.Clean(building.footprint);
            using (var output = GeoHomes.Build(new GeoBuildingContext(building), 0.2f))
                foreach (var part in output.parts)
                    foreach (var v in part.mesh.vertices)
                        Assert.IsTrue(Within(outline, new Vector2(v.x, v.z), 0.05f), message + ": " + new Vector2(v.x, v.z) + " is outside the footprint");
        }

        [Test] public void APlanHoldsWhatAModelHasBeyondItsWalls()
        {
            var model = Extended("T01", new[] { -1.5f, 1.5f, 0, 2.4f, 3.5f, 5.5f });   // a porch 2 m deep in front
            var plan = GeoHomePlan.Of(model, "gabled_along");
            Assert.AreEqual(10f, plan.bounds.width, 0.2f);
            Assert.AreEqual(-3.5f, plan.bounds.yMin, 0.2f);
            Assert.AreEqual(5.5f, plan.bounds.yMax, 0.2f, "the porch is part of the plan");
            Assert.AreEqual(70 + 6, plan.area, 3f, "the walls' rectangle and the porch, not the box around them");
            Assert.GreaterOrEqual(plan.rects.Length, 2);
        }

        [Test] public void AModelWithAPorchStaysInsideItsFootprint()
        {
            var model = Extended("T01", new[] { -1.5f, 1.5f, 0, 2.4f, 3.5f, 5.5f });
            var building = Building(length: 12, width: 10.8f, turn: 30);
            building.homeLibrary = Library(model);
            GeoHomes.Assign(building, new GenerationSettings { homeLibrary = building.homeLibrary, homeModels = GeoHomeUse.IntactAndUnknown }, (GeoHomeSite)null);
            Assert.AreEqual(model, building.home, "a 12 x 10.8 m footprint holds the 10 x 9 m plan stretched 1.2 times");
            AssertWithinFootprint(building, model, "porch");
        }

        [Test] public void AnLModelTurnsItsWingToTheWingOfTheOutline()
        {
            // main block 12 x 8 m and a wing behind it at one end; the footprint has its wing at the other end, in front
            var model = Model("T01", length: 12, width: 8);
            model.body = Blocks(0, new[] { -6f, 6f, 0, 2.8f, -4f, 4f }, new[] { 2f, 6f, 0, 2.8f, -6.5f, -4f });
            model.variants = new[] { Box(-6, 6, 2.8f, 5.0f, -4, 4, 1), Box(-6, 6, 2.8f, 3.2f, -4, 4, 1) };
            var building = Building(turn: 0);
            building.footprint = new List<Vector2> { new Vector2(-6, -4), new Vector2(6, -4), new Vector2(6, 4), new Vector2(-2, 4), new Vector2(-2, 6.5f), new Vector2(-6, 6.5f) };
            var library = Library(model);
            var fit = library.ChooseFit(building, GeoPolygon.Clean(building.footprint), GeoHomeUse.IntactAndUnknown, null);
            Assert.AreEqual(model, fit.model);
            Assert.IsTrue(fit.turn && !fit.mirror && !fit.quarter, "turned round, the wing comes to the outline's wing (" + fit.turn + ", " + fit.mirror + ", " + fit.quarter + ")");
            building.home = fit.model; building.homeLibrary = library; building.homeTurn = fit.turn; building.homeMirror = fit.mirror; building.homeQuarter = fit.quarter;
            AssertWithinFootprint(building, model, "wing");
        }

        [Test] public void AModelKeepsOutOfTheNeighboursOutline()
        {
            // an L footprint (0.84 of its rectangle) and a neighbour standing in the corner the L leaves open
            var building = Building(turn: 0);
            building.footprint = new List<Vector2> { new Vector2(-6, -4), new Vector2(6, -4), new Vector2(6, 4), new Vector2(-2, 4), new Vector2(-2, 6.5f), new Vector2(-6, 6.5f) };
            var outline = GeoPolygon.Clean(building.footprint);
            var box = Model("T01", length: 12, width: 10.5f);
            Assert.AreEqual(box, Library(box).Choose(building, outline, GeoHomeUse.IntactAndUnknown), "alone, a plain box may fill the corner");
            var site = new GeoHomeSite();
            var world = building.transform;
            site.Add("osm/way/7", new[] { new Vector2(-1.8f, 4.2f), new Vector2(6, 4.2f), new Vector2(6, 9), new Vector2(-1.8f, 9) }
                .Select(p => { var w = world.TransformPoint(new Vector3(p.x, 0, p.y)); return new Vector2(w.x, w.z); }).ToArray());
            Assert.IsNull(Library(box).ChooseFit(building, outline, GeoHomeUse.IntactAndUnknown, site).model, "the box would stand in the neighbour's house");
            var l = Model("T02", length: 12, width: 8);
            l.body = Blocks(0, new[] { -6f, 6f, 0, 2.8f, -4f, 4f }, new[] { 2f, 6f, 0, 2.8f, -6.5f, -4f });
            l.variants = new[] { Box(-6, 6, 2.8f, 5.0f, -4, 4, 1), Box(-6, 6, 2.8f, 3.2f, -4, 4, 1) };
            Assert.AreEqual(l, Library(box, l).ChooseFit(building, outline, GeoHomeUse.IntactAndUnknown, site).model, "the L leaves the corner free");
        }

        [Test] public void TheFrontFacesTheStreetAlongTheLongSideElseTheSouth()
        {
            var model = Model();
            var building = Building(length: 12, width: 8.4f, turn: 0);
            var library = Library(model);
            var outline = GeoPolygon.Clean(building.footprint);
            var north = new GeoHomeSite { street = p => new Vector2(0, 1) };
            var fit = library.ChooseFit(building, outline, GeoHomeUse.IntactAndUnknown, north);
            Assert.Greater(Vector2.Dot(GeoHomes.Front(GeoRuinFit.FrameOf(outline), fit.quarter, fit.turn), new Vector2(0, 1)), 0.9f, "the street to the north");
            var none = library.ChooseFit(building, outline, GeoHomeUse.IntactAndUnknown, new GeoHomeSite());
            Assert.Greater(Vector2.Dot(GeoHomes.Front(GeoRuinFit.FrameOf(outline), none.quarter, none.turn), new Vector2(0, -1)), 0.9f, "no street: to the south");
            var east = new GeoHomeSite { street = p => new Vector2(1, 0) };
            var gable = library.ChooseFit(building, outline, GeoHomeUse.IntactAndUnknown, east);
            Assert.Greater(Vector2.Dot(GeoHomes.Front(GeoRuinFit.FrameOf(outline), gable.quarter, gable.turn), new Vector2(0, -1)), 0.9f, "the gable to the street: the porch to the south, into the yard");
        }

        [Test] public void FootwaysPathsAndTracksAreNotStreets()
        {
            LineFeature Road(string type, string kind, float z) => new LineFeature { type = type, kind = kind, points = new[] { -50f, z, 50f, z } };
            var streets = new GeoStreets(new[] { Road("footway", "path", 3), Road("track", "road", -4), Road("residential", "road", 20) });
            var toward = streets.Toward(Vector2.zero);
            Assert.IsNotNull(toward);
            Assert.Greater(toward.Value.y, 0.99f, "the residential street 20 m to the north, not the footway at 3 m nor the track at 4 m");
            Assert.IsFalse(GeoStreets.IsStreet(Road("path", "path", 0)));
            Assert.IsTrue(GeoStreets.IsStreet(Road("service", "road", 0)));
        }
    }
}
