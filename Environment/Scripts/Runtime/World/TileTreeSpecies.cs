using System.Collections.Generic;
using GeoWorld;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Виды деревьев на тайлах GeoWorld по месту (TreeSpeciesProfile). Пакет ставит на тайл три префаба (бук 01/03, тополь)
    /// и выбирает их только по высоте: дубрава, сосновый бор, лесополоса и сад выглядели одинаково. Здесь каждому дереву —
    /// вид по тому, где оно стоит:
    ///  - род места — как у GeoTreeHeights (лес: лес/кусты и лес на 45 % земли в 50 м; посёлок: застройка в клетке или на
    ///    10 % земли в 60 м; сад; иначе ряд — лесополоса, деревья среди полей);
    ///  - лес: тип листвы по контурам лесов OSM (leaf_type), где его нет — песок вокруг (сосновые боры на песчаных террасах);
    ///  - у воды и на болоте — прибрежные;
    /// высота дерева сохраняется (её задал GeoTreeHeights), вариант вида — ближайший по своей высоте (масштаб 0.5..2).
    /// Как: на sceneLoaded тайла его запуск Nature Renderer (GeoNatureRendererStart) придерживается; когда загрузчик деревьев
    /// готов, в его копию TerrainData (не ассет) дописываются прототипы видов и меняются деревья — по кускам за кадр; затем
    /// запуск отпускается, и Nature Renderer начинает уже с новыми деревьями. Одевание травой и точка взлёта ждут этого
    /// (Ready). Один на сцену (на объекте LocationsManager).
    /// </summary>
    public sealed class TileTreeSpecies : MonoBehaviour
    {
        private const string ProfilePath = "World/TreeSpeciesProfile";
        private const int TreesPerFrame = 6000;
        private const float Margin = 130f;
        // подбор варианта по высоте: из подходящих в 1.3 раза от лучшего — по хешу (как GeoTreeHeights)
        private const float FitSpread = 1.3f;
        private const float MinScale = 0.5f;
        private const float MaxScale = 2f;
        // бурелом: дороги дальше этого от края тайла не достанет (вершина лежит до ~20 м от пня); поворотов на пробу
        private const float LyingReach = 30f;
        private const int LyingTurns = 8;

        private static readonly HashSet<Terrain> Pending = new();

        private readonly List<Tile> _tiles = new();
        private TreeSpeciesProfile _profile;

        /// <summary>Виды на этом террейне уже расставлены (или он не тайл GeoWorld с деревьями).</summary>
        public static bool Ready(Terrain terrain) => terrain == null || !Pending.Contains(terrain);

        public static int ProcessedTiles { get; private set; }
        public static int ChangedTrees { get; private set; }

        private sealed class Tile
        {
            public Terrain Terrain;
            public GeoTerrainTreeLoader Loader;
            public GeoNatureRendererStart Start;
            public bool StartWasEnabled;
            // Nature Renderer уже рисовал тайл, когда мы его взяли (тайл загрузился раньше нас): запуск не трогаем
            public bool WasDrawing;
            public TreeInstance[] Trees;
            public int Next;
            public Context Context;
            public TreePrototype[] Prototypes;
            public float[] OriginalHeights;
            public int Changed;
        }

        // Карта земли тайла с полем: классы в клетках и суммы по площади (лес, застройка, известная земля, песок, вода).
        private sealed class Context
        {
            public GeoLandCover Cover;
            public float Cell;
            public int I0, J0, Width, Depth;
            public GeoLandClass[] Classes;
            public int[] Forest, Built, Known, Sand, Wet;
            public List<TreeSpeciesProfile.LeafArea> Leaves = new();
            public List<SpeciesChoice>[] Variants;
            // дороги у тайла и за его краем на LyingReach — для бурелома (TurnOffRoads); null — дорог нет
            public RoadAreas.Segment[] Roads;

            public int Box(int[] table, int a0, int a1, int b0, int b1)
            {
                int w = Width + 1;
                return table[(b1 + 1) * w + a1 + 1] - table[b0 * w + a1 + 1] - table[(b1 + 1) * w + a0] + table[b0 * w + a0];
            }

            public float Share(int[] table, int i, int j, int r)
            {
                int a0 = Mathf.Max(0, i - r), a1 = Mathf.Min(Width - 1, i + r), b0 = Mathf.Max(0, j - r), b1 = Mathf.Min(Depth - 1, j + r);
                int count = Box(Known, a0, a1, b0, b1);
                return count > 0 ? Box(table, a0, a1, b0, b1) / (float)count : 0f;
            }
        }

        private struct SpeciesChoice
        {
            public int Prototype;
            public float Height;
            // у лежащего (TreeSpeciesProfile.Species.lying) — ящик LOD0 по xz префаба: min x, min z, max x, max z
            public Vector4 Footprint;
        }

        private void Awake()
        {
            _profile = Resources.Load<TreeSpeciesProfile>(ProfilePath);

            if (_profile == null)
                Debug.LogWarning($"[TileTreeSpecies] Profile is missing: Resources/{ProfilePath}", this);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;

            // тайлы, загруженные раньше (их Nature Renderer, может быть, уже рисует — перезапустим после замены)
            for (int i = 0; i < SceneManager.sceneCount; i++)
                Hold(SceneManager.GetSceneAt(i));
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;

            foreach (Tile tile in _tiles)
                Release(tile, false);

            _tiles.Clear();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Hold(scene);

        private void Hold(Scene scene)
        {
            if (_profile == null || !scene.isLoaded)
                return;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (GeoNatureRendererStart start in root.GetComponentsInChildren<GeoNatureRendererStart>(true))
                {
                    Terrain terrain = start.GetComponent<Terrain>();
                    GeoTerrainTreeLoader loader = start.GetComponent<GeoTerrainTreeLoader>();

                    if (terrain == null || loader == null || Pending.Contains(terrain))
                        continue;

                    Behaviour natureRenderer = terrain.GetComponent("NatureRenderer") as Behaviour;
                    Tile tile = new()
                    {
                        Terrain = terrain, Loader = loader, Start = start, StartWasEnabled = start.enabled,
                        WasDrawing = start.enabled && !start.Waiting && natureRenderer != null && natureRenderer.enabled
                    };

                    // запуск Nature Renderer ждёт видов: OnDisable лишь снимает ожидание, сам он уже выключен в Awake. Уже
                    // рисующий тайл перезапустим в Release: выключить и включить запуск — это ещё раз выключить NR
                    if (!tile.WasDrawing)
                        start.enabled = false;

                    Pending.Add(terrain);
                    _tiles.Add(tile);
                }
            }
        }

        private void Update()
        {
            // по одному тайлу за раз: подготовка карты и куски деревьев не накладываются в одном кадре
            for (int k = 0; k < _tiles.Count; k++)
            {
                Tile tile = _tiles[k];

                if (tile.Terrain == null || tile.Loader == null)
                {
                    Pending.Remove(tile.Terrain);
                    _tiles.RemoveAt(k);
                    return;
                }

                if (!tile.Loader.Ready)
                    continue;

                if (Step(tile))
                {
                    Release(tile, true);
                    _tiles.RemoveAt(k);
                }

                return;
            }
        }

        // true — тайл готов.
        private bool Step(Tile tile)
        {
            TerrainData data = tile.Terrain.terrainData;

            if (tile.Trees == null)
            {
                // деревья лежат только в копии загрузчика; ассет TerrainData (в редакторе — файл тайла) не трогаем
                if (data == null || data.treeInstanceCount == 0 || !IsRuntimeData(data))
                    return true;

                tile.Context = BuildContext(tile.Terrain);

                if (tile.Context == null)
                    return true;

                tile.OriginalHeights = GeoTreeHeights.PrototypeHeights(data.treePrototypes);
                tile.Prototypes = ExtendPrototypes(data.treePrototypes, tile.Context);
                tile.Trees = data.treeInstances;
                return false;
            }

            Vector3 origin = tile.Terrain.transform.position;
            Vector3 size = data.size;
            int end = Mathf.Min(tile.Next + TreesPerFrame, tile.Trees.Length);

            for (int i = tile.Next; i < end; i++)
            {
                if (Assign(ref tile.Trees[i], origin, size, tile))
                    tile.Changed++;
            }

            tile.Next = end;

            if (tile.Next < tile.Trees.Length)
                return false;

            if (tile.Changed > 0)
            {
                data.treePrototypes = tile.Prototypes;
                data.SetTreeInstances(tile.Trees, false);
                tile.Terrain.Flush();
            }

            ProcessedTiles++;
            ChangedTrees += tile.Changed;
            return true;
        }

        private void Release(Tile tile, bool restartRunning)
        {
            Pending.Remove(tile.Terrain);

            if (tile.Start == null)
                return;

            // Nature Renderer уже рисовал этот тайл (тайл загрузился раньше нас) — только перезапуск на новых деревьях;
            // включение запуска после него снова выключало NR на несколько кадров (и перезапуск пропадал зря)
            if (tile.WasDrawing)
            {
                if (restartRunning && tile.Changed > 0 && tile.Terrain != null)
                    NatureRendererTerrain.Restart(tile.Terrain);

                return;
            }

            if (tile.StartWasEnabled)
                tile.Start.enabled = true;
        }

        private static bool IsRuntimeData(TerrainData data) =>
            data.name.EndsWith("(Clone)") || _Game.Scripts.Locations.CustomSpawnRuntimeEnvironmentGenerator.IsRuntimeCopy(data);

        // Прототипы тайла остаются на своих местах (на их индексы опираются GeoTreeHeights и точка взлёта), виды — после.
        private TreePrototype[] ExtendPrototypes(TreePrototype[] original, Context context)
        {
            List<TreePrototype> prototypes = new(original);
            context.Variants = new List<SpeciesChoice>[_profile.species.Length];

            for (int s = 0; s < _profile.species.Length; s++)
            {
                context.Variants[s] = new List<SpeciesChoice>();

                foreach (GameObject prefab in _profile.species[s].variants)
                {
                    if (prefab == null)
                        continue;

                    int index = prototypes.FindIndex(p => p.prefab == prefab);

                    if (index < 0)
                    {
                        prototypes.Add(new TreePrototype { prefab = prefab });
                        index = prototypes.Count - 1;
                    }

                    context.Variants[s].Add(new SpeciesChoice
                    {
                        Prototype = index, Height = GeoTreeHeights.PrefabHeight(prefab),
                        Footprint = _profile.species[s].lying ? LyingFootprint(prefab) : Vector4.zero
                    });
                }
            }

            return prototypes.ToArray();
        }

        private Context BuildContext(Terrain terrain)
        {
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            GeoLandCover cover = GeoLandCover.At(origin.x + size.x * 0.5f, origin.z + size.z * 0.5f);

            if (cover == null)
                return null;

            Context c = new() { Cover = cover, Cell = cover.cellSize > 0f ? cover.cellSize : 10f };
            c.I0 = Mathf.FloorToInt((origin.x - Margin - cover.origin.x) / c.Cell);
            c.J0 = Mathf.FloorToInt((origin.z - Margin - cover.origin.y) / c.Cell);
            c.Width = Mathf.CeilToInt((size.x + Margin * 2f) / c.Cell) + 1;
            c.Depth = Mathf.CeilToInt((size.z + Margin * 2f) / c.Cell) + 1;
            int w1 = c.Width + 1;
            c.Classes = new GeoLandClass[c.Width * c.Depth];
            c.Forest = new int[w1 * (c.Depth + 1)];
            c.Built = new int[w1 * (c.Depth + 1)];
            c.Known = new int[w1 * (c.Depth + 1)];
            c.Sand = new int[w1 * (c.Depth + 1)];
            c.Wet = new int[w1 * (c.Depth + 1)];

            for (int j = 0; j < c.Depth; j++)
            {
                float z = cover.origin.y + (c.J0 + j + 0.5f) * c.Cell;

                for (int i = 0; i < c.Width; i++)
                {
                    float x = cover.origin.x + (c.I0 + i + 0.5f) * c.Cell;
                    cover.Sample(x, z, out GeoLandClass value);
                    c.Classes[j * c.Width + i] = value;
                    int s = (j + 1) * w1 + i + 1, left = s - 1, below = s - w1, corner = s - w1 - 1;
                    c.Forest[s] = (value == GeoLandClass.Forest ? 1 : 0) + c.Forest[left] + c.Forest[below] - c.Forest[corner];
                    c.Built[s] = (Built(value) ? 1 : 0) + c.Built[left] + c.Built[below] - c.Built[corner];
                    c.Known[s] = (value != GeoLandClass.None ? 1 : 0) + c.Known[left] + c.Known[below] - c.Known[corner];
                    c.Sand[s] = (value == GeoLandClass.Sand ? 1 : 0) + c.Sand[left] + c.Sand[below] - c.Sand[corner];
                    c.Wet[s] = (value == GeoLandClass.Water || value == GeoLandClass.Wetland ? 1 : 0) + c.Wet[left] + c.Wet[below] - c.Wet[corner];
                }
            }

            Rect tileRect = new(origin.x - 10f, origin.z - 10f, size.x + 20f, size.z + 20f);

            // контуры лесов — свои у каждого мира (WorldMapData); мир без них — по песку вокруг
            TreeLeafAreas leafAreas = WorldMapData.LeafAreas;

            foreach (TreeSpeciesProfile.LeafArea area in leafAreas != null ? leafAreas.areas : System.Array.Empty<TreeSpeciesProfile.LeafArea>())
            {
                if (area != null && area.bounds.Overlaps(tileRect))
                    c.Leaves.Add(area);
            }

            RoadAreas roads = RoadAreas.Instance;

            if (roads != null)
            {
                c.Roads = roads.SegmentsIn(new Rect(origin.x - LyingReach, origin.z - LyingReach, size.x + LyingReach * 2f, size.z + LyingReach * 2f));

                if (c.Roads.Length == 0)
                    c.Roads = null;
            }

            return c;
        }

        private static bool Built(GeoLandClass value) =>
            value == GeoLandClass.Urban || value == GeoLandClass.Building || value == GeoLandClass.Asphalt || value == GeoLandClass.Paving;

        private bool Assign(ref TreeInstance tree, Vector3 origin, Vector3 size, Tile tile)
        {
            Context c = tile.Context;
            float x = origin.x + tree.position.x * size.x;
            float z = origin.z + tree.position.z * size.z;
            int i = Mathf.FloorToInt((x - c.Cover.origin.x) / c.Cell) - c.I0;
            int j = Mathf.FloorToInt((z - c.Cover.origin.y) / c.Cell) - c.J0;

            if (i < 0 || j < 0 || i >= c.Width || j >= c.Depth || tree.prototypeIndex >= tile.OriginalHeights.Length)
                return false;

            GeoLandClass value = c.Classes[j * c.Width + i];

            if (value == GeoLandClass.None)
                return false;

            TreeSpeciesProfile.Mix mix = PickMix(c, value, i, j, x, z);

            if (mix == null || mix.weights == null || mix.weights.Length == 0)
                return false;

            int species = PickSpecies(mix, c, Hash(x, z, 31), true);

            if (species < 0)
                return false;

            float height = tree.heightScale * Mathf.Max(tile.OriginalHeights[tree.prototypeIndex], 0.1f);
            float aspect = tree.heightScale > 1e-4f ? tree.widthScale / tree.heightScale : 1f;
            Choose(c, species, height, x, z, out SpeciesChoice choice, out float scale);

            // бурелом лежит вершиной на земле, а дерево тайла стоит где угодно, и у дороги тоже: поворачиваем так, чтобы
            // вершина легла мимо дороги, а где так не выходит — здесь стоячий вид
            if (_profile.species[species].lying && !TurnOffRoads(c, choice.Footprint, scale * aspect, x, z, ref tree.rotation))
            {
                species = PickSpecies(mix, c, Hash(x, z, 31), false);

                if (species < 0)
                    return false;

                Choose(c, species, height, x, z, out choice, out scale);
            }

            tree.prototypeIndex = choice.Prototype;
            tree.heightScale = scale;
            tree.widthScale = scale * aspect;
            return true;
        }

        private void Choose(Context c, int species, float height, float x, float z, out SpeciesChoice choice, out float scale)
        {
            if (_profile.species[species].ownHeight)
            {
                // снаг: свой вариант по хешу и почти свой размер
                List<SpeciesChoice> variants = c.Variants[species];
                choice = variants[Mathf.Min((int)(Hash(x, z, 37) * variants.Count), variants.Count - 1)];
                scale = 0.85f + 0.3f * Hash(x, z, 41);
                return;
            }

            choice = PickVariant(c.Variants[species], height, Hash(x, z, 37));
            scale = Mathf.Clamp(height / Mathf.Max(choice.Height, 0.1f), MinScale, MaxScale);
        }

        // Поворот (рад, как TreeInstance.rotation: Nature Renderer рисует Quaternion.Euler(0, rotation°, 0)), при котором
        // ящик лежащего дерева (footprint, × widthScale) не ближе RoadGap к полотну дорог: сначала свой, затем через 45°.
        // false — так не лежит никак.
        private static bool TurnOffRoads(Context c, Vector4 footprint, float widthScale, float x, float z, ref float rotation)
        {
            if (c.Roads == null)
                return true;

            Vector2 min = new Vector2(footprint.x, footprint.y) * widthScale;
            Vector2 max = new Vector2(footprint.z, footprint.w) * widthScale;
            float reachX = Mathf.Max(-min.x, max.x);
            float reachZ = Mathf.Max(-min.y, max.y);
            float reach = Mathf.Sqrt(reachX * reachX + reachZ * reachZ) + DetailSpacing.RoadGap;
            Vector2 root = new(x, z);
            List<RoadAreas.Segment> near = null;

            foreach (RoadAreas.Segment segment in c.Roads)
            {
                if (RoadAreas.EdgeDistance(segment, root) < reach)
                    (near ??= new List<RoadAreas.Segment>()).Add(segment);
            }

            if (near == null)
                return true;

            for (int k = 0; k < LyingTurns; k++)
            {
                float angle = rotation + k * Mathf.PI * 2f / LyingTurns;

                if (ClearOfRoads(near, min, max, root, angle))
                {
                    rotation = angle;
                    return true;
                }
            }

            return false;
        }

        private static bool ClearOfRoads(List<RoadAreas.Segment> roads, Vector2 min, Vector2 max, Vector2 root, float angle)
        {
            float cos = Mathf.Cos(angle);
            float sin = Mathf.Sin(angle);

            foreach (RoadAreas.Segment segment in roads)
            {
                if (SegmentBoxDistance(ToLocal(segment.A), ToLocal(segment.B), min, max) < segment.HalfWidth + DetailSpacing.RoadGap)
                    return false;
            }

            return true;

            // мир в ось префаба: обратный поворот вокруг Y
            Vector2 ToLocal(Vector2 point)
            {
                float dx = point.x - root.x;
                float dz = point.y - root.y;
                return new Vector2(dx * cos - dz * sin, dx * sin + dz * cos);
            }
        }

        // Расстояние от отрезка ab до прямоугольника [min, max]; 0 — задевает его.
        private static float SegmentBoxDistance(Vector2 a, Vector2 b, Vector2 min, Vector2 max)
        {
            Vector2 d = b - a;
            float t0 = 0f, t1 = 1f;

            if (Clip(-d.x, a.x - min.x, ref t0, ref t1) && Clip(d.x, max.x - a.x, ref t0, ref t1) &&
                Clip(-d.y, a.y - min.y, ref t0, ref t1) && Clip(d.y, max.y - a.y, ref t0, ref t1))
                return 0f;

            float distance = Mathf.Min(PointBoxDistance(a, min, max), PointBoxDistance(b, min, max));
            distance = Mathf.Min(distance, PointSegmentDistance(min, a, b));
            distance = Mathf.Min(distance, PointSegmentDistance(max, a, b));
            distance = Mathf.Min(distance, PointSegmentDistance(new Vector2(min.x, max.y), a, b));
            return Mathf.Min(distance, PointSegmentDistance(new Vector2(max.x, min.y), a, b));
        }

        // Liang–Barsky: отсечение параметра отрезка одной стороной прямоугольника.
        private static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 1e-9f)
                return q >= 0f;

            float r = q / p;

            if (p < 0f)
            {
                if (r > t1)
                    return false;

                t0 = Mathf.Max(t0, r);
            }
            else
            {
                if (r < t0)
                    return false;

                t1 = Mathf.Min(t1, r);
            }

            return true;
        }

        private static float PointBoxDistance(Vector2 p, Vector2 min, Vector2 max)
        {
            float dx = Mathf.Max(Mathf.Max(min.x - p.x, 0f), p.x - max.x);
            float dz = Mathf.Max(Mathf.Max(min.y - p.y, 0f), p.y - max.y);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float length = ab.sqrMagnitude;
            float t = length > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / length) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        // Ящик LOD0 префаба по xz его корня (min x, min z, max x, max z), с масштабом корня.
        private static Vector4 LyingFootprint(GameObject prefab)
        {
            LODGroup lod = prefab.GetComponentInChildren<LODGroup>();
            Renderer[] renderers = lod != null && lod.lodCount > 0 ? lod.GetLODs()[0].renderers : prefab.GetComponentsInChildren<Renderer>();
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);

            foreach (Renderer renderer in renderers)
            {
                MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;

                if (filter == null || filter.sharedMesh == null)
                    continue;

                Matrix4x4 matrix = toRoot * renderer.transform.localToWorldMatrix;
                Bounds b = filter.sharedMesh.bounds;

                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((k & 1) * 2 - 1, ((k >> 1) & 1) * 2 - 1, ((k >> 2) & 1) * 2 - 1));
                    Vector3 point = matrix.MultiplyPoint3x4(corner);
                    min = Vector2.Min(min, new Vector2(point.x, point.z));
                    max = Vector2.Max(max, new Vector2(point.x, point.z));
                }
            }

            if (min.x > max.x)
                return Vector4.zero;

            float rootScale = Mathf.Abs(prefab.transform.localScale.x);
            return new Vector4(min.x, min.y, max.x, max.y) * rootScale;
        }

        private TreeSpeciesProfile.Mix PickMix(Context c, GeoLandClass value, int i, int j, float x, float z)
        {
            if (value == GeoLandClass.Orchard)
                return _profile.orchard;

            int wetCells = Mathf.Max(1, Mathf.RoundToInt(_profile.wetRadius / c.Cell));

            if (value == GeoLandClass.Water || value == GeoLandClass.Wetland || c.Share(c.Wet, i, j, wetCells) >= _profile.wetShare)
                return _profile.wet;

            int forestCells = Mathf.Max(1, Mathf.RoundToInt(GeoTreeHeights.ForestRadius / c.Cell));

            if ((value == GeoLandClass.Forest || value == GeoLandClass.Scrub) && c.Share(c.Forest, i, j, forestCells) >= GeoTreeHeights.ForestShare)
            {
                byte leaf = LeafTypeAt(c, x, z);

                if (leaf == 2)
                    return _profile.forestConifer;

                if (leaf == 3)
                    return _profile.forestMixed;

                if (leaf == 1)
                    return _profile.forestBroadleaf;

                int sandCells = Mathf.Max(1, Mathf.RoundToInt(_profile.sandRadius / c.Cell));
                return c.Share(c.Sand, i, j, sandCells) >= _profile.sandShare ? _profile.forestSandy : _profile.forestBroadleaf;
            }

            int builtCells = Mathf.Max(1, Mathf.RoundToInt(GeoTreeHeights.BuiltRadius / c.Cell));

            if (Built(value) || c.Share(c.Built, i, j, builtCells) >= GeoTreeHeights.BuiltShare)
                return _profile.settlement;

            return _profile.rows;
        }

        private static byte LeafTypeAt(Context c, float x, float z)
        {
            foreach (TreeSpeciesProfile.LeafArea area in c.Leaves)
            {
                if (!area.bounds.Contains(new Vector2(x, z)) || !Inside(area.outer, 0, area.outer.Length, x, z))
                    continue;

                bool inHole = false;

                for (int h = 0; h < area.holeStarts.Length && !inHole; h++)
                {
                    int start = area.holeStarts[h];
                    int end = h + 1 < area.holeStarts.Length ? area.holeStarts[h + 1] : area.holes.Length;
                    inHole = Inside(area.holes, start, end, x, z);
                }

                if (!inHole)
                    return area.type;
            }

            return 0;
        }

        private static bool Inside(Vector2[] ring, int start, int end, float x, float z)
        {
            bool inside = false;

            for (int a = start, b = end - 1; a < end; b = a++)
            {
                Vector2 p = ring[a], q = ring[b];

                if ((p.y > z) != (q.y > z) && x < (q.x - p.x) * (z - p.y) / (q.y - p.y) + p.x)
                    inside = !inside;
            }

            return inside;
        }

        // lying false — без лежащих видов (бурелом там, где он лёг бы на дорогу)
        private int PickSpecies(TreeSpeciesProfile.Mix mix, Context c, float random, bool lying)
        {
            float total = 0f;

            for (int s = 0; s < mix.weights.Length && s < c.Variants.Length; s++)
            {
                if (c.Variants[s].Count > 0 && (lying || !_profile.species[s].lying))
                    total += Mathf.Max(0f, mix.weights[s]);
            }

            if (total <= 0f)
                return -1;

            float pick = random * total;

            for (int s = 0; s < mix.weights.Length && s < c.Variants.Length; s++)
            {
                if (c.Variants[s].Count == 0 || mix.weights[s] <= 0f || !lying && _profile.species[s].lying)
                    continue;

                pick -= mix.weights[s];

                if (pick <= 0f)
                    return s;
            }

            return -1;
        }

        private static SpeciesChoice PickVariant(List<SpeciesChoice> variants, float height, float random)
        {
            float best = float.MaxValue;

            foreach (SpeciesChoice v in variants)
                best = Mathf.Min(best, Mathf.Abs(Mathf.Log(height / Mathf.Max(v.Height, 0.1f))));

            float limit = best + Mathf.Log(FitSpread);
            int count = 0;

            foreach (SpeciesChoice v in variants)
            {
                if (Mathf.Abs(Mathf.Log(height / Mathf.Max(v.Height, 0.1f))) <= limit)
                    count++;
            }

            int pick = Mathf.Min(count - 1, (int)(random * count));

            foreach (SpeciesChoice v in variants)
            {
                if (Mathf.Abs(Mathf.Log(height / Mathf.Max(v.Height, 0.1f))) > limit)
                    continue;

                if (pick-- == 0)
                    return v;
            }

            return variants[0];
        }

        // 0..1 от места (до сантиметра) и соли — как GeoTreeHeights, но со своими солями.
        private static float Hash(float x, float z, int salt)
        {
            unchecked
            {
                uint h = (uint)Mathf.RoundToInt(x * 100) * 73856093u ^ (uint)Mathf.RoundToInt(z * 100) * 19349663u ^ (uint)salt * 83492791u;
                h ^= h >> 13;
                h *= 0x5bd1e995u;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            Pending.Clear();
            ProcessedTiles = 0;
            ChangedTrees = 0;
        }
    }
}
