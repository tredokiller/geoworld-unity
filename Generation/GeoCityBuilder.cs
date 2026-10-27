using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Bakes the buildings of a whole city into chunks of mesh. One GameObject per building is fine for a village
    // but not for twenty thousand houses, so here a chunk of a few hundred metres is one object with one mesh:
    // every building is built by the same code as an editable one (footprint, roof, facade, damage) and its
    // triangles are merged. Colours go through a palette texture, so all walls and roofs share one material.
    // Buildings drawn with a ruin model keep the model's textures: each model used in a chunk is one more sub-mesh.
    public static class GeoCityBuilder
    {
        const int PaletteWidth = 256, PaletteHeight = 128; // 32 x 32 x 32 colours
        static readonly Vector4 NoTangent = new Vector4(1, 0, 0, 1);

        sealed class Chunk
        {
            public readonly List<Vector3> vertices = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public readonly List<Vector4> tangents = new List<Vector4>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> opaque = new List<int>(), glass = new List<int>();
            // triangles of ruin models, by material
            public readonly Dictionary<Material, List<int>> ruins = new Dictionary<Material, List<int>>();
            public readonly List<GeoCityChunk.Entry> entries = new List<GeoCityChunk.Entry>();
        }

        public static bool ShouldMerge(GenerationSettings settings, int buildingCount) =>
            settings.buildingMode == GeoBuildingMode.Merged ||
            (settings.buildingMode == GeoBuildingMode.Auto && buildingCount > settings.mergeAbove);

        // Returns the number of buildings baked. `group` receives one child per chunk. streets (optional) turns ruin
        // models to face their street.
        public static int Build(IEnumerable<BuildingFeature> sources, GeoBounds bounds, Transform group, Func<Vector3, float> ground,
            Action<Object> save, List<string> notes, GenerationSettings settings, Action<float> progress, int total, GeoStreets streets = null)
        {
            Func<Vector2, Vector2?> street = streets != null && !streets.Empty ? p => streets.Toward(p) : (Func<Vector2, Vector2?>)null;
            var chunks = new Dictionary<Vector2Int, Chunk>();
            var temp = new GameObject("Geo building (temporary)") { hideFlags = HideFlags.HideAndDontSave };
            int count = 0, skipped = 0, damaged = 0, index = 0;
            var ruins = new Dictionary<string, int>();
            var homes = new Dictionary<string, int>();
            float size = Mathf.Max(50, settings.chunkSize);
            // homes are only stretched, never cut: each is drawn as the library's own mesh with its own transform
            var homeGroups = new Dictionary<Vector2Int, Transform>();
            int instanced = 0;
            GeoRuinFit.ClearCache();
            // thousands of copies of the library models: drawn with their reduced versions
            var reduced = settings.reducedModels && (settings.ruinLibrary || settings.homeLibrary)
                ? GeoGeneration.Target.ReducedModels(settings.ruinLibrary, settings.homeLibrary) : null;
            GeoModelDetail.Use(reduced);
            try
            {
                foreach (var listed in sources)
                {
                    if (progress != null && index++ % 250 == 0) progress(index / (float)Mathf.Max(1, total));
                    if (!GeoWorldBuild.ValidBuilding(listed) || !bounds.Contains(listed.x, listed.z)) { skipped++; continue; }
                    var source = listed;
                    var building = temp.AddComponent<GeoBuilding>();
                    try
                    {
                        source = GeoWorldBuild.ForSettings(source, settings);
                        GeoBuildingBuilder.Import(building, source, null);
                        GeoRuins.Assign(building, settings, street);
                        GeoHomes.Assign(building, settings, street);
                        Simplify(building, source);
                        var context = new GeoBuildingContext(building);
                        GeoBuildingBuilder.Ground(building, context, ground, out float baseY, out float depth);
                        temp.transform.position = new Vector3(temp.transform.position.x, baseY, temp.transform.position.z);
                        var key = new Vector2Int(Mathf.FloorToInt(source.x / size), Mathf.FloorToInt(source.z / size));
                        if (!chunks.TryGetValue(key, out var chunk)) chunks[key] = chunk = new Chunk();
                        if (settings.instancedHomes && GeoHomes.Draws(building))
                        {
                            if (!homeGroups.TryGetValue(key, out var homeGroup))
                            {
                                homeGroup = new GameObject("Homes " + key.x + "_" + key.y).transform; homeGroup.SetParent(group, false);
                                homeGroups[key] = homeGroup;
                            }
                            PlaceHome(building, context, depth, temp.transform, homeGroup, source.id);
                            AppendPlinth(chunk, context, depth, temp.transform.localToWorldMatrix);
                            instanced++;
                        }
                        else
                            using (var output = GeoBuildingGeometry.Build(building, depth, GeoBuildingBuilder.Local(building, ground, baseY)))
                                Append(chunk, building, output, temp.transform.localToWorldMatrix);
                        var state = GeoBuildingDamage.StateOf(building);
                        if (state >= GeoConditionState.Damaged) damaged++;
                        string ruin = GeoRuins.Draws(building) ? building.ruin.id : null;
                        if (ruin != null) ruins[ruin] = ruins.TryGetValue(ruin, out int used) ? used + 1 : 1;
                        if (GeoHomes.Draws(building)) { string home = building.home.id; homes[home] = homes.TryGetValue(home, out int usedHome) ? usedHome + 1 : 1; }
                        chunk.entries.Add(new GeoCityChunk.Entry
                        {
                            id = source.id, category = source.category, name = source.name,
                            position = temp.transform.position, state = state,
                            basis = building.condition != null ? building.condition.basis : null, ruin = ruin
                        });
                        count++;
                    }
                    catch (ArgumentException e)
                    {
                        skipped++;
                        if (notes != null && skipped <= 5) notes.Add("Building " + source.id + " skipped: " + e.Message);
                    }
                    finally { Object.DestroyImmediate(building); }
                }
            }
            finally { Object.DestroyImmediate(temp); GeoModelDetail.Use(null); }

            var palette = Palette(save);
            var glass = GlassMaterial(save);
            foreach (var pair in chunks)
            {
                var chunk = pair.Value;
                if (chunk.vertices.Count == 0) continue;
                var mesh = new Mesh { name = "Buildings " + pair.Key.x + "_" + pair.Key.y, indexFormat = chunk.vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(chunk.vertices);
                mesh.SetNormals(chunk.normals);
                mesh.SetUVs(0, chunk.uvs);
                // ruin models from the same chunk in a fixed order: the same city always bakes the same mesh
                var models = new List<Material>(chunk.ruins.Keys);
                models.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                if (models.Count > 0) mesh.SetTangents(chunk.tangents);
                mesh.subMeshCount = 2 + models.Count;
                mesh.SetTriangles(chunk.opaque, 0);
                mesh.SetTriangles(chunk.glass, 1);
                for (int i = 0; i < models.Count; i++) mesh.SetTriangles(chunk.ruins[models[i]], 2 + i);
                mesh.RecalculateBounds();
                // the parts with repeating textures start near 0 (AppendRuin), so their coordinates fit 16 bits too
                mesh = GeoMeshCompression.Compact(mesh, GeoMeshCompression.RepeatingUv);
                save(mesh);
                var go = new GameObject("Chunk " + pair.Key.x + "_" + pair.Key.y);
                go.transform.SetParent(group, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                var materials = new List<Material> { palette, glass };
                materials.AddRange(models);
                renderer.sharedMaterials = materials.ToArray();
                if (settings.mergedColliders) go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.AddComponent<GeoCityChunk>().buildings = chunk.entries.ToArray();
            }
            foreach (var homeGroup in homeGroups.Values) Capture(homeGroup, save);
            if (skipped > 0 && notes != null) notes.Add(skipped + " buildings outside bounds or with invalid outlines were left out of the merged city.");
            if (notes != null)
            {
                notes.Add("Merged city: " + count + " buildings baked into " + chunks.Count + " chunks of " + size + " m (" + damaged + " with damage). " +
                          "Select a chunk to see its buildings; use Editable mode for one object per building.");
                if (ruins.Count > 0) notes.Add(RuinNote(ruins));
                if (homes.Count > 0) notes.Add(HomeNote(homes));
                if (instanced > 0) notes.Add(instanced + " homes are instances of the library's models (an object each with the model's own meshes and a transform), not copies of their geometry. " +
                                             "They are kept as a list in the world's asset and made when the scene loads (a few per frame while playing), not saved in the scene.");
                if (reduced && (ruins.Count > 0 || homes.Count > 0)) notes.Add("Ruin and home models are drawn with their reduced copies: " + GeoModelDetail.Summary(reduced));
            }
            return count;
        }

        // Small buildings do without window and door geometry: it would triple the size of a city of houses.
        static void Simplify(GeoBuilding building, BuildingFeature source)
        {
            bool big = source.levels >= 3 || source.width * source.length >= 220 || building.category == "apartments" || building.category == "industrial" || building.category == "public";
            if (!big) { building.windows = false; building.entrance = false; }
        }

        // "N buildings are drawn with ruin models: B09 x 40, B13 x 12 ..."
        internal static string RuinNote(Dictionary<string, int> uses)
        {
            var list = new List<KeyValuePair<string, int>>(uses);
            list.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            int total = 0; var parts = new List<string>();
            foreach (var pair in list) { total += pair.Value; parts.Add(pair.Key + " x " + pair.Value); }
            return total + " damaged buildings are drawn with ruin models fitted to their footprints: " + string.Join(", ", parts) + ".";
        }

        // "N buildings are drawn with home models: N01 x 40, N11 x 12 ..."
        internal static string HomeNote(Dictionary<string, int> uses)
        {
            var list = new List<KeyValuePair<string, int>>(uses);
            list.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            int total = 0; var parts = new List<string>();
            foreach (var pair in list) { total += pair.Value; parts.Add(pair.Key + " x " + pair.Value); }
            return total + " buildings are drawn with home models fitted to their footprints, with the roofs of the export: " + string.Join(", ", parts) + ".";
        }

        // ------------------------------------------------------------------ homes as instances

        // Where a home model sits in its building's local space: the same affine map GeoHomes.Build applies to every
        // vertex (stretched to the footprint's rectangle along its axis and across it, walls to the eaves, the roof
        // about the eave line), as matrices for the body (and the fixed roof) and for the roof variant. Only the foot
        // differs: a baked home pulls its lowest vertices down to the foundation, an instance stands on a plinth.
        public static void HomeInstance(GeoBuilding building, GeoBuildingContext context, float depth,
            out Matrix4x4 body, out Matrix4x4 roof, out Mesh variant, out Material roofMaterial)
        {
            var model = building.home; var roofInfo = building.roofInfo;
            string name = GeoHomeModel.VariantFor(roofInfo?.shape, roofInfo?.renderAs, roofInfo?.ridge, model.defaultVariant);
            if (model.Variant(name) == null) name = model.defaultVariant;
            float roofRise = roofInfo != null && GeoRoofInfo.Has(roofInfo.rise) ? roofInfo.rise : building.roofHeight;
            bool flat = name == "flat";
            float height = GeoHomes.DrawnHeight(model, building.height, roofRise, flat);
            var p = GeoHomes.Measure(model, context.outline, height, roofRise, flat, building.homeTurn, building.homeMirror, depth);
            float flip = p.turn ? -1f : 1f, mirror = p.mirror ? -1f : 1f;
            var x = new Vector4(p.axis.x, 0, p.axis.y, 0) * (p.sx * flip * mirror);
            var z = new Vector4(p.across.x, 0, p.across.y, 0) * (p.sz * flip);
            var centre = new Vector4(p.centre.x, 0, p.centre.y, 1);
            body = new Matrix4x4(x, new Vector4(0, p.sy, 0, 0), z, centre);
            roof = new Matrix4x4(x, new Vector4(0, p.sr, 0, 0), z, centre + new Vector4(0, p.eaveTarget - p.eaveModel * p.sr, 0, 0));
            variant = model.Variant(name);
            roofMaterial = GeoHomes.RoofMaterial(building.homeLibrary, model, roofInfo);
        }

        // A transform for a matrix whose columns are a horizontal axis, the vertical and the horizontal axis across it,
        // each scaled (the first may be a mirror image).
        public static void Place(Transform transform, Matrix4x4 world)
        {
            Vector3 x = world.GetColumn(0), y = world.GetColumn(1), z = world.GetColumn(2);
            var rotation = Quaternion.LookRotation(z.normalized, y.normalized);
            float sx = Vector3.Dot(rotation * Vector3.right, x) >= 0 ? x.magnitude : -x.magnitude;
            transform.SetPositionAndRotation(world.GetColumn(3), rotation);
            transform.localScale = new Vector3(sx, y.magnitude, z.magnitude);
        }

        // One object per home, its renderers the library's own meshes and materials (the GPU Resident Drawer draws the
        // thousands of them that share a mesh and material together).
        static void PlaceHome(GeoBuilding building, GeoBuildingContext context, float depth, Transform frame, Transform parent, string id)
        {
            HomeInstance(building, context, depth, out var body, out var roof, out var variant, out var roofMaterial);
            var model = building.home;
            var home = new GameObject("Home " + model.id + " | " + id).transform;
            home.SetParent(parent, false);
            void Part(string name, Mesh mesh, Matrix4x4 local)
            {
                if (!mesh) return;
                var go = new GameObject(name);
                go.transform.SetParent(home, false);
                Place(go.transform, frame.localToWorldMatrix * local);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                // sub-mesh 0 the model's baked atlas, 1 the roof surface in the export's roof material (as GeoHomes.Build)
                var materials = new Material[mesh.subMeshCount];
                for (int s = 0; s < materials.Length; s++) materials[s] = s == 1 && roofMaterial ? roofMaterial : model.material;
                go.AddComponent<MeshRenderer>().sharedMaterials = materials;
            }
            Part("Body", model.body, body);
            Part("Fixed roof", model.roofFixed, body);
            Part("Roof", variant, roof);
            // left out once it is a few pixels tall (a 10 m house at about 2.8 km with the preview camera)
            var lod = home.gameObject.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(HomeCull, home.GetComponentsInChildren<Renderer>()) });
            lod.RecalculateBounds();
        }

        // share of the screen's height below which a home is not drawn
        public const float HomeCull = 0.004f;

        // Turns the home objects placed under a group into a GeoHomeSet in the world's asset and a GeoHomeInstances
        // that makes the same objects again whenever the scene loads, so the scene does not hold thousands of them.
        static void Capture(Transform group, Action<Object> save)
        {
            var set = ScriptableObject.CreateInstance<GeoHomeSet>();
            set.name = group.name;
            set.cullHeight = HomeCull;
            for (int i = group.childCount - 1; i >= 0; i--)
            {
                var home = group.GetChild(i);
                var parts = new List<GeoHomeSet.Part>();
                foreach (Transform part in home)
                {
                    var filter = part.GetComponent<MeshFilter>();
                    var renderer = part.GetComponent<MeshRenderer>();
                    if (!filter || !renderer) continue;
                    parts.Add(new GeoHomeSet.Part
                    {
                        name = part.name, mesh = filter.sharedMesh, materials = renderer.sharedMaterials,
                        position = part.localPosition, rotation = part.localRotation, scale = part.localScale
                    });
                }
                set.homes.Add(new GeoHomeSet.Home { name = home.name, parts = parts.ToArray() });
                Object.DestroyImmediate(home.gameObject);
            }
            set.homes.Reverse();
            save(set);
            var instances = group.gameObject.AddComponent<GeoHomeInstances>();
            instances.set = set;
            instances.Remake();
        }

        static readonly Color Plinth = new Color(0.45f, 0.44f, 0.40f);

        // A plain plinth under an instanced home on sloping ground, from the ground's lowest point under the footprint up
        // into the walls, just inside the footprint's rectangle.
        static void AppendPlinth(Chunk chunk, GeoBuildingContext context, float depth, Matrix4x4 toWorld)
        {
            if (depth < 0.05f) return;
            var frame = GeoRuinFit.FrameOf(context.outline);
            float halfL = frame.length * 0.5f - 0.02f, halfW = frame.width * 0.5f - 0.02f;
            if (halfL <= 0 || halfW <= 0) return;
            Vector2 a = frame.axis, b = frame.Across, c = frame.centre;
            var corners = new[] { c - a * halfL - b * halfW, c + a * halfL - b * halfW, c + a * halfL + b * halfW, c - a * halfL + b * halfW };
            var uv = PaletteUv(Plinth);
            for (int i = 0; i < 4; i++)
            {
                Vector2 p = corners[i], q = corners[(i + 1) % 4];
                var outward = new Vector3(q.y - p.y, 0, p.x - q.x).normalized;
                if (Vector2.Dot(new Vector2(outward.x, outward.z), (p + q) * 0.5f - c) < 0) outward = -outward;
                var normal = toWorld.MultiplyVector(outward).normalized;
                int first = chunk.vertices.Count;
                foreach (var v in new[] { new Vector3(p.x, -depth, p.y), new Vector3(q.x, -depth, q.y), new Vector3(q.x, 0.15f, q.y), new Vector3(p.x, 0.15f, p.y) })
                {
                    chunk.vertices.Add(toWorld.MultiplyPoint3x4(v)); chunk.normals.Add(normal); chunk.tangents.Add(NoTangent); chunk.uvs.Add(uv);
                }
                // faces turned to look outward
                var n = Vector3.Cross(chunk.vertices[first + 1] - chunk.vertices[first], chunk.vertices[first + 2] - chunk.vertices[first]);
                if (Vector3.Dot(n, normal) >= 0) chunk.opaque.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
                else chunk.opaque.AddRange(new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
            }
        }

        // A ruin or home model keeps its own texture coordinates, tangents and material.
        static void AppendRuin(Chunk chunk, GeoBuildingPart part, Matrix4x4 toWorld)
        {
            var mesh = part.mesh;
            var vertices = mesh.vertices; var normals = mesh.normals; var tangents = mesh.tangents; var uvs = mesh.uv; var triangles = mesh.triangles;
            if (!chunk.ruins.TryGetValue(part.material, out var list)) chunk.ruins[part.material] = list = new List<int>();
            // a repeating texture (a roof in metres) looks the same moved by whole repeats: start the part near 0
            var texture = part.material.mainTexture;
            var shift = Vector2.zero;
            if (texture && texture.wrapMode == TextureWrapMode.Repeat && uvs.Length > 0)
            {
                float minU = float.MaxValue, minV = float.MaxValue;
                foreach (var uv in uvs) { minU = Mathf.Min(minU, uv.x); minV = Mathf.Min(minV, uv.y); }
                shift = new Vector2(Mathf.Floor(minU), Mathf.Floor(minV));
            }
            int first = chunk.vertices.Count;
            for (int i = 0; i < vertices.Length; i++)
            {
                chunk.vertices.Add(toWorld.MultiplyPoint3x4(vertices[i]));
                chunk.normals.Add(toWorld.MultiplyVector(normals[i]).normalized);
                var t = i < tangents.Length ? tangents[i] : NoTangent;
                var direction = toWorld.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                chunk.tangents.Add(new Vector4(direction.x, direction.y, direction.z, t.w));
                chunk.uvs.Add(i < uvs.Length ? uvs[i] - shift : Vector2.zero);
            }
            foreach (int t in triangles) list.Add(first + t);
        }

        static void Append(Chunk chunk, GeoBuilding building, GeoBuildingOutput output, Matrix4x4 toWorld)
        {
            uint seed = GeoBuildingDamage.Seed(building.sourceId ?? building.name);
            var state = GeoBuildingDamage.StateOf(building);
            foreach (var part in output.parts)
            {
                if ((part.name == "Ruin" || part.name == "Model") && part.material) { AppendRuin(chunk, part, toWorld); continue; }
                bool isGlass = part.name == "Windows";
                var vertices = part.mesh.vertices; var normals = part.mesh.normals; var triangles = part.mesh.triangles;
                Color color = Color.gray;
                if (!isGlass)
                {
                    bool wall = part.name == "Walls" && building.overrideWallColor && !building.wallMaterial;
                    bool roof = part.name == "Roof" && building.overrideRoofColor && !building.roofMaterial;
                    color = wall ? building.wallColor : roof ? building.roofColor : GeoBuildingBuilder.PartColor(part.material);
                    color = GeoBuildingDamage.Tint(state, part.name, color, seed);
                    // a little variation so a street of identical houses does not look printed
                    float v = 0.94f + 0.12f * GeoBuildingDamage.Hash01(seed, 99);
                    color = new Color(color.r * v, color.g * v, color.b * v);
                }
                var uv = isGlass ? Vector2.zero : PaletteUv(color);
                bool bottomFaces = part.name != "Foundation";
                var keep = new bool[vertices.Length];
                var list = isGlass ? chunk.glass : chunk.opaque;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    // the underside of a foundation is never seen
                    if (!bottomFaces && normals[triangles[i]].y < -0.5f) continue;
                    for (int k = 0; k < 3; k++) keep[triangles[i + k]] = true;
                }
                var remap = new int[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (!keep[i]) continue;
                    remap[i] = chunk.vertices.Count;
                    chunk.vertices.Add(toWorld.MultiplyPoint3x4(vertices[i]));
                    chunk.normals.Add(toWorld.MultiplyVector(normals[i]).normalized);
                    chunk.tangents.Add(NoTangent);
                    chunk.uvs.Add(uv);
                }
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    if (!bottomFaces && normals[triangles[i]].y < -0.5f) continue;
                    list.Add(remap[triangles[i]]); list.Add(remap[triangles[i + 1]]); list.Add(remap[triangles[i + 2]]);
                }
            }
        }

        // Colours are quantised to 5 bits per channel; the palette texture holds every one of them.
        internal static Vector2 PaletteUv(Color color)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(color.r) * 31), 0, 31);
            int g = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(color.g) * 31), 0, 31);
            int b = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(color.b) * 31), 0, 31);
            int index = r * 1024 + g * 32 + b;
            return new Vector2(((index % PaletteWidth) + 0.5f) / PaletteWidth, ((index / PaletteWidth) + 0.5f) / PaletteHeight);
        }

        public static Color PaletteColor(int index)
        {
            int r = index / 1024, g = index / 32 % 32, b = index % 32;
            return new Color(r / 31f, g / 31f, b / 31f);
        }

        // The one material every merged chunk of buildings and props shares.
        internal static Material PaletteMaterial(Action<Object> save) => Palette(save);

        static Material Palette(Action<Object> save)
        {
            var texture = new Texture2D(PaletteWidth, PaletteHeight, TextureFormat.RGBA32, false, false)
            { name = "City palette", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[PaletteWidth * PaletteHeight];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = PaletteColor(i);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            save(texture);
            var shader = GeoAssets.Shader("Universal Render Pipeline/Lit") ?? GeoAssets.Shader("Standard");
            var material = new Material(shader) { name = "City buildings", enableInstancing = true };
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            material.mainTexture = texture;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.08f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            save(material);
            return material;
        }

        static Material GlassMaterial(Action<Object> save)
        {
            var shader = GeoAssets.Shader("Universal Render Pipeline/Lit") ?? GeoAssets.Shader("Standard");
            var color = new Color(0.12f, 0.23f, 0.28f);
            var material = new Material(shader) { name = "City window glass", enableInstancing = true, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.7f);
            save(material);
            return material;
        }
    }
}
