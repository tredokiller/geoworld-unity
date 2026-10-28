using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // World packages (runtime generation): the riverside sample made into a package of 200 m tiles with the code a player runs
    // (GeoWorldGenerator, GeoMemoryTarget), its manifest, the same bytes from the same export and settings, and its tiles loaded
    // back as the objects the generator made, with colliders and navigation markers for a game's NavMesh.
    public class GeoWorldPackageTests
    {
        const string Sample = "Assets/GeoWorld/Samples/RiversideVillage";
        readonly List<string> folders = new List<string>();
        readonly List<GeoWorldTileHandle> handles = new List<GeoWorldTileHandle>();
        readonly List<Object> owned = new List<Object>();

        [TearDown] public void Cleanup()
        {
            foreach (var h in handles) h.Unload();
            handles.Clear();
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var f in folders) if (Directory.Exists(f)) Directory.Delete(f, true);
            folders.Clear();
        }

        // no satellite imagery (no network in a test); the libraries (homes, ruins, trees, road pictures) as in a game
        static GenerationSettings Settings() => new GenerationSettings
        {
            heightResolution = 129, textureResolution = 128, roadPaintResolution = 128, satelliteImagery = false, maxProps = 4000
        };

        string Temp(string name)
        {
            string folder = Path.Combine(Path.GetTempPath(), "geoworld-package-tests", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            folders.Add(folder);
            return folder;
        }

        GeoWorldPackage Make(string output, string name = "riverside") =>
            GeoWorldGenerator.BuildPackage(Path.GetFullPath(Sample), new GeoWorldRequest { name = name, settings = Settings(), tileSize = 200, outputFolder = output });

        [Test] public void Package_HasItsManifestAndEveryTile()
        {
            var package = Make(Temp("manifest"));
            var env = GeoData.ReadEnvironment(Sample + "/environment.json");
            var m = package.Manifest;
            Assert.That(m.format, Is.EqualTo("geoworld-package"));
            Assert.That(m.formatVersion, Is.EqualTo(GeoWorldPackage.FormatVersion));
            Assert.That(m.generator, Does.EndWith(GeoWorldGenerator.Version));
            Assert.That(m.tileVersion, Is.EqualTo(GeoTileGeometry.Version));
            Assert.That(m.origin.lat, Is.EqualTo(env.origin.lat)); Assert.That(m.origin.lon, Is.EqualTo(env.origin.lon));
            Assert.That(m.origin.crs, Is.EqualTo("local-enu-wgs84"));
            Assert.That(m.tiles.Length, Is.EqualTo(GeoTilePlan.Make(new[] { env.bounds }, 200).tiles.Count));
            Assert.That(m.tiles.All(t => File.Exists(Path.Combine(package.Folder, t.file)) && t.sha256.Length == 64));
            Assert.That(m.stats.buildings, Is.GreaterThan(0));
            Assert.That(m.stats.relief, Is.GreaterThanOrEqualTo(0));
            Assert.That(m.stats.forest + m.stats.builtUp + m.stats.water + m.stats.open, Is.EqualTo(1).Within(0.001f));
            Assert.That(m.inputHash.Length, Is.EqualTo(64)); Assert.That(m.contentHash.Length, Is.EqualTo(64));
            Assert.That(m.settings.homeLibrary, Is.Null, "the package keeps no references to assets");
            Assert.That(package.Verify(out var problems), Is.True, string.Join(", ", problems));
            Assert.That(File.Exists(Path.Combine(package.Folder, "generation-report.json")));
            Assert.That(Directory.Exists(package.Folder + ".part"), Is.False, "the package is moved in place when it is complete");
        }

        [Test] public void Package_IsTheSameForTheSameExportAndSettings()
        {
            var a = Make(Temp("first"));
            var b = Make(Temp("second"));
            Assert.That(b.Manifest.contentHash, Is.EqualTo(a.Manifest.contentHash));
            Assert.That(b.Manifest.inputHash, Is.EqualTo(a.Manifest.inputHash));
            for (int i = 0; i < a.Tiles.Count; i++) Assert.That(b.Tiles[i].sha256, Is.EqualTo(a.Tiles[i].sha256), a.Tiles[i].file);
            // and a change of the settings is another package
            var settings = Settings(); settings.decorationSeed = 9;
            var c = GeoWorldGenerator.BuildPackage(Path.GetFullPath(Sample), new GeoWorldRequest { name = "riverside", settings = settings, tileSize = 200, outputFolder = Temp("third") });
            Assert.That(c.Manifest.inputHash, Is.Not.EqualTo(a.Manifest.inputHash));
        }

        [Test] public void Package_ChangedTileFails()
        {
            var package = Make(Temp("verify"));
            var tile = package.Tiles[0];
            string path = Path.Combine(package.Folder, tile.file);
            var bytes = File.ReadAllBytes(path); bytes[bytes.Length - 1] ^= 0xff; File.WriteAllBytes(path, bytes);
            Assert.That(package.Verify(out var problems), Is.False);
            Assert.That(problems, Has.Member("changed " + tile.file));
        }

        [Test] public void LoadedTile_IsWhatTheGeneratorMade()
        {
            var package = Make(Temp("load"));
            var busiest = package.Tiles.OrderByDescending(t => t.buildings).First();
            var handle = package.LoadTile(busiest.Index, null, new GeoTileLoadOptions { buildingColliders = false, navigationTags = false });
            handles.Add(handle);

            // the same tile made again in memory, as BuildPackage makes it
            var settings = GeoWorldBuild.Sanitize(GeoWorldBuild.TileSettings(Settings()));
            settings.homeLibrary = GeoAssets.Load<GeoHomeLibrary>(GeoHomeLibrary.DefaultPath); settings.ruinLibrary = GeoAssets.Load<GeoRuinLibrary>(GeoRuinLibrary.DefaultPath);
            var made = new List<Object>();
            GeoWorldRoot fresh;
            using (GeoGeneration.Use(new GeoMemoryTarget(null, default)))
            {
                var source = GeoTileSource.Prepare(Sample + "/environment.json", Sample + "/buildings.json", settings, 200, Vector2.zero);
                var job = source.Job(busiest.Index, GeoTilePlan.Tile(busiest.Index, 200), "", "Tile");
                fresh = GeoWorldBuild.Build(Sample + "/environment.json", Sample + "/buildings.json", settings, null, job, "", o => made.Add(o));
            }
            owned.Add(fresh.gameObject); owned.AddRange(made);

            var loaded = handle.Root.GetComponent<GeoWorldRoot>();
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.buildingCount, Is.EqualTo(fresh.buildingCount));
            Assert.That(loaded.tileIndex, Is.EqualTo(busiest.Index));
            Assert.That(loaded.terrain, Is.Not.Null);
            Assert.That(Saved(handle.Root).Count, Is.EqualTo(Saved(fresh.gameObject).Count), "the same objects");
            Assert.That(Meshes(handle.Root), Is.EqualTo(Meshes(fresh.gameObject)), "the same meshes (vertices and triangles)");
            Assert.That(Shaders(handle.Root), Is.EqualTo(Shaders(fresh.gameObject)), "the same materials");
            Assert.That(handle.Root.GetComponentsInChildren<GeoHomeInstances>(true).Sum(h => h.Count), Is.EqualTo(fresh.GetComponentsInChildren<GeoHomeInstances>(true).Sum(h => h.Count)));
            Assert.That(handle.Root.GetComponentsInChildren<GeoCityChunk>(true).Sum(c => c.buildings.Length), Is.EqualTo(fresh.GetComponentsInChildren<GeoCityChunk>(true).Sum(c => c.buildings.Length)));
            Assert.That(handle.Root.GetComponentsInChildren<GeoInstancedMeshes>(true).Sum(i => i.count), Is.EqualTo(fresh.GetComponentsInChildren<GeoInstancedMeshes>(true).Sum(i => i.count)));
            // library meshes are the library's own (by key), not copies
            var homeParts = handle.Root.GetComponentsInChildren<GeoHomeInstances>(true).SelectMany(h => h.set.homes).SelectMany(h => h.parts).ToList();
            if (homeParts.Count > 0)
            {
                Assert.That(homeParts.All(p => p.mesh && UnityEditor.EditorUtility.IsPersistent(p.mesh)), "home parts are the library's meshes");
                // the roofs tinted to the export's colours are the package's own materials (in the editor they are assets of the project)
                var tinted = homeParts.SelectMany(p => p.materials).Where(m => m && !UnityEditor.EditorUtility.IsPersistent(m)).ToList();
                Assert.That(tinted, Is.Not.Empty, "tinted roofs");
                Assert.That(tinted.All(m => m.GetTexture("_BaseMap") && m.GetColor("_BaseColor") != Color.white && m.GetColor("_BaseColor").a > 0.99f && m.GetFloat("_ZWrite") > 0.5f),
                    "a tinted roof keeps its picture, takes its colour and the rest of its source's values");
                var freshParts = fresh.GetComponentsInChildren<GeoHomeInstances>(true).SelectMany(h => h.set.homes).SelectMany(h => h.parts).ToList();
                Assert.That(Colours(homeParts), Is.EqualTo(Colours(freshParts)), "the same roof colours as the generator's");
            }
            // the ground: heights to 16 bits of the shared range
            var a = loaded.terrain.terrainData; var b = fresh.terrain.terrainData;
            Assert.That(a.heightmapResolution, Is.EqualTo(b.heightmapResolution)); Assert.That(a.size, Is.EqualTo(b.size));
            var ha = a.GetHeights(0, 0, a.heightmapResolution, a.heightmapResolution); var hb = b.GetHeights(0, 0, b.heightmapResolution, b.heightmapResolution);
            float worst = 0;
            for (int z = 0; z < a.heightmapResolution; z++) for (int x = 0; x < a.heightmapResolution; x++) worst = Mathf.Max(worst, Mathf.Abs(ha[z, x] - hb[z, x]));
            Assert.That(worst * a.size.y, Is.LessThan(0.01f), "heights within a centimetre");
            Assert.That(loaded.terrain.transform.position, Is.EqualTo(fresh.terrain.transform.position));
            Assert.That(a.terrainLayers.Length, Is.EqualTo(b.terrainLayers.Length));
        }

        [Test] public void LoadedTile_HasCollidersAndNavigationMarkers()
        {
            var package = Make(Temp("navigation"));
            var busiest = package.Tiles.OrderByDescending(t => t.buildings).First();
            var handle = package.LoadTile(busiest.Index); handles.Add(handle);
            Assert.That(handle.Navigation.buildings.Length, Is.GreaterThan(0));
            Assert.That(handle.Navigation.buildings.All(b => b.outline.Length >= 3 && b.height > 0));
            var tile = package.Tile(busiest.Index);
            Assert.That(handle.Navigation.buildings.All(b => { var c = Centre(b.outline); return c.x >= tile.minX - 50 && c.x <= tile.maxX + 50 && c.y >= tile.minZ - 50 && c.y <= tile.maxZ + 50; }));
            var group = handle.Root.transform.Find(GeoNavigation.Group);
            Assert.That(group, Is.Not.Null);
            var colliders = group.GetComponentsInChildren<MeshCollider>();
            Assert.That(colliders.Length, Is.GreaterThan(0));
            Assert.That(colliders.All(c => c.GetComponent<GeoNavigationTag>().surface == GeoSurface.Building && !c.GetComponent<GeoNavigationTag>().walkable));
            // a ray down onto a building's middle hits its collider, its top at the building's height
            var b0 = handle.Navigation.buildings.OrderByDescending(b => Mathf.Abs(GeoPolygon.Area(b.outline))).First();
            var centre = GeoPolygon.Contains(b0.outline, Centre(b0.outline)) ? Centre(b0.outline) : b0.outline[0];
            Physics.SyncTransforms();
            var scene = handle.Root.scene.GetPhysicsScene();
            Assert.That(scene.Raycast(new Vector3(centre.x, b0.baseY + b0.height + 50, centre.y), Vector3.down, out var hit, 500), Is.True);
            Assert.That(hit.point.y, Is.EqualTo(b0.baseY + b0.height).Within(0.6f));
            var terrain = handle.Terrain;
            Assert.That(terrain.GetComponent<GeoNavigationTag>().surface, Is.EqualTo(GeoSurface.Ground));
            Assert.That(terrain.GetComponent<TerrainCollider>(), Is.Not.Null);
            // and without them
            handle.Unload();
            var bare = package.LoadTile(busiest.Index, null, new GeoTileLoadOptions { colliders = false, buildingColliders = false, navigationTags = false }); handles.Add(bare);
            Assert.That(bare.Root.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(bare.Root.GetComponentsInChildren<GeoNavigationTag>(true), Is.Empty);
        }

        [Test] public void BuildingColliders_FarFromTheOrigin()
        {
            // a tile 5 km out: its outlines are world metres, beyond the 2 km GeoPolygon takes in a building's own metres (a world of
            // 20 x 20 km failed on every tile more than 2 km from its middle); an outline that is not one is left out, not the tile
            var root = new GameObject("Far tile"); owned.Add(root);
            var navigation = new GeoTileNavigation { buildings = new[]
            {
                new GeoTileNavigation.Building { id = "far", baseY = 10, height = 12,
                    outline = new[] { new Vector2(5000, 3000), new Vector2(5020, 3000), new Vector2(5020, 3010), new Vector2(5000, 3010) } },
                new GeoTileNavigation.Building { id = "no area", baseY = 10, height = 5,
                    outline = new[] { new Vector2(5100, 3000), new Vector2(5100, 3000), new Vector2(5100, 3000) } },
            } };
            var created = new List<Object>();
            Assert.DoesNotThrow(() => GeoNavigation.BuildingColliders(root.transform, navigation, created));
            owned.AddRange(created);
            var colliders = root.GetComponentsInChildren<MeshCollider>();
            Assert.That(colliders.Length, Is.EqualTo(1));
            Assert.That(colliders[0].sharedMesh.bounds.center.x, Is.EqualTo(5010).Within(0.01f), "put back in world metres");
            Physics.SyncTransforms();
            Assert.That(root.scene.GetPhysicsScene().Raycast(new Vector3(5010, 100, 3005), Vector3.down, out var hit, 500), Is.True);
            Assert.That(hit.point.y, Is.EqualTo(22).Within(0.01f), "the roof at the building's height");
        }

        [Test] public void Unload_DestroysWhatTheTileMade()
        {
            var package = Make(Temp("unload"));
            var handle = package.LoadTile(package.Tiles[0].Index);
            var meshes = handle.Root.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh).Where(m => m && !UnityEditor.EditorUtility.IsPersistent(m)).ToList();
            var data = handle.Terrain.terrainData;
            handle.Unload();
            Assert.That(handle.Loaded, Is.False);
            Assert.That(meshes.All(m => !m), "the tile's own meshes are destroyed");
            Assert.That(!data, "and its terrain data");
        }

        [Test] public void Validate_TakesOneExportAtMost()
        {
            Assert.DoesNotThrow(() => GeoWorldGenerator.Validate(new GeoWorldRequest { south = 48.979, west = 37.798, north = 48.997, east = 37.826 }));
            Assert.Throws<ArgumentException>(() => GeoWorldGenerator.Validate(new GeoWorldRequest { south = 48.9, west = 37.7, north = 49.1, east = 38.0 }), "200 km² is more than one export");
            Assert.Throws<ArgumentException>(() => GeoWorldGenerator.Validate(new GeoWorldRequest { south = 49, west = 37, north = 48, east = 38 }));
        }

        [Test] public void Extractor_ArgumentsAreTheSameInEveryCulture()
        {
            var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");
                Assert.That(GeoExtractorRunner.Box(48.979, 37.798, 48.997, 37.826), Is.EqualTo("48.979,37.798,48.997,37.826"));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = culture; }
            Assert.That(GeoExtractorRunner.SafeName("Лиман: центр/1"), Is.EqualTo("Лиман__центр_1"));
            Assert.That(GeoExtractorRunner.SafeName("  "), Is.EqualTo("area"));
            Assert.That(GeoExtractorRunner.AppFolder, Is.Not.Null, "the package's own app in the editor");
        }

        [Test] public void Version_IsThePackagesVersion()
        {
            var json = File.ReadAllText(Path.GetFullPath(GeoPaths.Package + "/package.json"));
            Assert.That(json, Does.Contain("\"version\": \"" + GeoWorldGenerator.Version + "\""));
        }

        [Test] public void Registry_FindsAssetsByKeyAndPath()
        {
            var registry = ScriptableObject.CreateInstance<GeoRuntimeAssets>(); owned.Add(registry);
            var tree = GeoTerrainTrees.Prefabs()[0];
            var mesh = tree.GetComponent<MeshFilter>().sharedMesh;
            string path = UnityEditor.AssetDatabase.GetAssetPath(tree);
            string key = GeoAssets.Key(mesh);
            Assert.That(key, Does.Match("^[0-9a-f]{32}:-?[0-9]+$"));
            Assert.That(GeoAssets.Resolve(key), Is.SameAs(mesh));
            registry.entries.Add(new GeoRuntimeAssets.Entry { key = GeoAssets.Key(tree), path = path, asset = tree });
            registry.entries.Add(new GeoRuntimeAssets.Entry { key = key, path = UnityEditor.AssetDatabase.GetAssetPath(mesh), asset = mesh });
            var before = GeoAssets.Registry;
            try
            {
                GeoAssets.Registry = registry; GeoAssets.ForceRegistry = true;
                Assert.That(GeoAssets.Key(mesh), Is.EqualTo(key), "the same key as the editor's");
                Assert.That(GeoAssets.Resolve(key), Is.SameAs(mesh));
                Assert.That(GeoAssets.Load<GameObject>(path), Is.SameAs(tree));
                Assert.That(GeoPaths.Exists(path), Is.True);
                Assert.That(GeoPaths.Exists(GeoPaths.Package + "/nothing.png"), Is.False);
                Assert.That(GeoAssets.Key(new Mesh()), Is.Null, "a generated mesh has no key");
            }
            finally { GeoAssets.ForceRegistry = false; GeoAssets.Registry = before; }
        }

        static Vector2 Centre(Vector2[] outline) { var c = Vector2.zero; foreach (var p in outline) c += p; return c / outline.Length; }

        // the objects a tile keeps (not those its components make when they wake up)
        static List<Transform> Saved(GameObject root) =>
            root.GetComponentsInChildren<Transform>(true).Where(t => (t.gameObject.hideFlags & HideFlags.DontSaveInEditor) == 0 && !t.GetComponentInParent<GeoHomeInstances>(true) || t.GetComponent<GeoHomeInstances>()).ToList();

        static string Meshes(GameObject root) => string.Join(";", Saved(root).Select(t => t.GetComponent<MeshFilter>()).Where(f => f && f.sharedMesh)
            .Select(f => f.name + ":" + f.sharedMesh.vertexCount + "/" + Enumerable.Range(0, f.sharedMesh.subMeshCount).Sum(s => (long)f.sharedMesh.GetIndexCount(s))));

        static string Shaders(GameObject root) => string.Join(";", Saved(root).Select(t => t.GetComponent<MeshRenderer>()).Where(r => r)
            .SelectMany(r => r.sharedMaterials.Select(m => m ? m.shader.name + "|" + string.Join(",", m.shaderKeywords.OrderBy(k => k)) + "|" + Colour(m) + "|" + (m.mainTexture ? m.mainTexture.name : "") : "none")));

        static string Colour(Material m) => m.HasProperty("_BaseColor") ? ColorUtility.ToHtmlStringRGBA(m.GetColor("_BaseColor")) : "";

        static string Colours(IEnumerable<GeoHomeSet.Part> parts) => string.Join(";", parts.SelectMany(p => p.materials).Select(m => m ? m.name + "=" + Colour(m) : "none"));
    }
}
