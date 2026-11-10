using System.Collections.Generic;
using GeoWorld;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>Вид воды: у каждого свой шейдер (Game/Water/River, Lake, Swamp, Sea), лужи — в шейдере террейна.</summary>
    public enum WaterKind
    {
        River,
        Lake,
        Swamp,
        Sea
    }

    /// <summary>
    /// Вода GeoWorld: меши «Water bodies» загруженных тайлов (реки — одна поверхность на тайл с уклоном по течению,
    /// «Water | rivers»; озёра и пруды — плоские, «Water | &lt;id&gt;»).
    ///  - Высота воды под точкой: коллайдеров у воды нет, а террейн под рекой прорезан на глубину русла, поэтому берём её
    ///    из треугольников самой воды (при первом запросе к мешу — сетка ячеек со списками треугольников).
    ///  - Вид по воде: пакет даёт всей воде один материал — реки (Water (project), Game/Water/River);
    ///    стоячей воде ставим свой по тому, что это за вода: берег — болото по карте земель — Water (swamp), площадь от
    ///    2 км² (большое водохранилище, море) — Water (sea), иначе пруд или озеро — Water (lakes). Стоячая вода, которая
    ///    касается реки (старица, затон, разлив у русла — в OSM не water=river, и пакет рисует её отдельным плоским
    ///    озером), остаётся речной: шейдер и поток реки; иначе на стыке по клеткам сетки резала прямая граница тёмной
    ///    озёрной воды и мутной речной. Вид решается на весь водоём (по имени меша — id водоёма) по всем его загруженным
    ///    кускам вместе, разрезы по границам тайлов — не берег: каждый кусок решал сам, и один разлив был болотом в одном
    ///    тайле и озером в соседнем — квадрат по сетке тайлов. Загрузился ещё кусок и вид водоёма сменился — его куски
    ///    переодеваются. Тени вода не бросает — иначе темнит своё дно.
    ///  - Течение рек: шейдеру реки нужен поток в UV3 меша (м/с, мир xz). Считаем его по осям рек тайла (GeoPath с river,
    ///    точки — по течению, как линии рек OSM): на стрежне быстрее, у берегов медленнее — в свою копию меша (меш тайла
    ///    не трогаем).
    /// Новые тайлы ищутся раз в секунду (Update — от RotorWashWater, запрос высоты — тоже).
    /// </summary>
    public static class WaterSurfaces
    {
        private const float RescanInterval = 1f;
        private const float CellSize = 8f;
        private const string RiversName = "Water | rivers";
        // открытая вода — от этой площади (м²): на большом водохранилище ветер разгоняет волну, как на море
        private const float SeaArea = 2_000_000f;
        // болото — когда болото по карте земель хотя бы на такой доле берега (или внутри воды)
        private const float SwampShare = 0.35f;
        // на сколько метров от кромки смотреть берег и сколько точек берега брать
        private const float ShoreReach = 12f;
        private const int ShoreSamples = 160;
        // поток реки: ячейка сетки отрезков осей (м), как далеко от оси за берегом поток ещё есть (м)
        private const float FlowCellSize = 32f;
        private const float FlowReach = 30f;
        // стоячая вода касается реки, когда за её кромкой в JoinReach м — река, на JoinLength м кромки и больше
        private const float JoinReach = 2.5f;
        private const float JoinLength = 12f;

        private static readonly List<Surface> Surfaces = new();
        private static readonly HashSet<MeshFilter> Known = new();
        // стоячая вода по имени меша (id водоёма): замеры его кусков по тайлам, и выгруженных тоже
        private static readonly Dictionary<string, Body> Bodies = new();
        // рамки рек, загруженных за этот проход: стоячую воду рядом проверить на стык с ними
        private static readonly List<Bounds> NewRivers = new();
        // материал рек пакета: для кусков водоёма, которые уже получили свой, а потом оказались речными
        private static Material _riverMaterial;
        private static readonly List<MeshFilter> Filters = new();
        private static readonly List<GeoPath> Paths = new();
        private static readonly Material[] Materials = new Material[4];
        private static readonly bool[] MaterialsLoaded = new bool[4];
        private static readonly int[] KindCounts = new int[4];
        private static float _nextScan = float.NegativeInfinity;

        /// <summary>Подхватить воду новых тайлов (не чаще раза в секунду).</summary>
        public static void Update() => Rescan();

        /// <summary>Растёт, когда вода тайла появляется или выгружается (WaterShore тогда перерисовывает карту берега).</summary>
        public static int Version { get; private set; }

        /// <summary>Отрисовщики воды, рамка которых задевает область (мир xz).</summary>
        public static void CollectRenderers(Rect area, List<Renderer> into)
        {
            Rescan();
            into.Clear();

            foreach (Surface surface in Surfaces)
            {
                if (surface.Filter == null)
                    continue;

                Bounds bounds = surface.Bounds;

                if (bounds.max.x < area.xMin || bounds.min.x > area.xMax || bounds.max.z < area.yMin || bounds.min.z > area.yMax)
                    continue;

                if (surface.Filter.TryGetComponent(out MeshRenderer renderer) && renderer.enabled)
                    into.Add(renderer);
            }
        }

        /// <summary>Сколько воды каждого вида загружено (для проверок).</summary>
        public static string Stats
        {
            get
            {
                Rescan();
                System.Array.Clear(KindCounts, 0, KindCounts.Length);

                foreach (Surface surface in Surfaces)
                {
                    if (surface.Filter != null)
                        KindCounts[(int)surface.Kind]++;
                }

                return $"rivers={KindCounts[(int)WaterKind.River]} lakes={KindCounts[(int)WaterKind.Lake]} " +
                       $"swamps={KindCounts[(int)WaterKind.Swamp]} seas={KindCounts[(int)WaterKind.Sea]}";
            }
        }

        private sealed class Surface
        {
            public MeshFilter Filter;
            public WaterKind Kind;
            public Bounds Bounds;
            public Vector3[] Vertices;
            public int[] Triangles;
            public Dictionary<long, List<int>> Cells;
            // своя копия меша (с потоком реки), её уничтожаем вместе с тайлом
            public Mesh Owned;
            public GeoWorldRoot Root;
        }

        private sealed class Body
        {
            // по тайлу куска
            public readonly Dictionary<long, Piece> Pieces = new();
            // касается реки (хоть один кусок): речная вся, и куски в соседних тайлах
            public bool Joined;
        }

        // Замеры куска водоёма: площадь (м²), берег (м кромки: у суши, из них у болота), вода внутри (м²: под картой
        // земель, из них болото).
        private struct Piece
        {
            public float Area, ShoreLand, ShoreWetland, Inside, InsideWetland;
        }

        /// <summary>
        /// Высота воды под точкой (м, мир). Под точкой несколько поверхностей (мост реки над прудом) — ближайшая снизу
        /// к точке, а если точка под водой — самая высокая.
        /// </summary>
        public static bool TryGetHeight(Vector3 position, out float height) => TryGetHeight(position, out height, out _);

        /// <summary>Высота воды под точкой (м, мир) и вид этой воды.</summary>
        public static bool TryGetHeight(Vector3 position, out float height, out WaterKind kind)
        {
            Rescan();
            height = float.NegativeInfinity;
            kind = WaterKind.Lake;
            float below = float.NegativeInfinity;
            WaterKind belowKind = WaterKind.Lake;
            bool found = false;

            for (int i = Surfaces.Count - 1; i >= 0; i--)
            {
                Surface surface = Surfaces[i];

                if (surface.Filter == null)
                {
                    Forget(i);
                    continue;
                }

                Bounds bounds = surface.Bounds;

                if (position.x < bounds.min.x || position.x > bounds.max.x || position.z < bounds.min.z || position.z > bounds.max.z)
                    continue;

                if (!TryGetSurfaceHeight(surface, position, out float surfaceHeight))
                    continue;

                found = true;

                if (surfaceHeight > height)
                {
                    height = surfaceHeight;
                    kind = surface.Kind;
                }

                if (surfaceHeight <= position.y && surfaceHeight > below)
                {
                    below = surfaceHeight;
                    belowKind = surface.Kind;
                }
            }

            if (found && below > float.NegativeInfinity)
            {
                height = below;
                kind = belowKind;
            }

            return found;
        }

        private static void Rescan()
        {
            if (Time.unscaledTime < _nextScan)
                return;

            _nextScan = Time.unscaledTime + RescanInterval;

            // вода выгруженных тайлов: и пока дрон высоко (запросов высоты нет), её меши и сетки не держим
            for (int i = Surfaces.Count - 1; i >= 0; i--)
            {
                if (Surfaces[i].Filter == null)
                    Forget(i);
            }

            foreach (GeoWorldRoot root in Object.FindObjectsByType<GeoWorldRoot>(FindObjectsSortMode.None))
            {
                Transform group = root.transform.Find(GeoWaterLook.Group);

                if (group == null)
                    continue;

                Filters.Clear();
                group.GetComponentsInChildren(true, Filters);
                // реки тайла — первыми: стоячей воде, которая их касается, нужны их меши
                Filters.Sort((a, b) => (b.gameObject.name == RiversName).CompareTo(a.gameObject.name == RiversName));

                foreach (MeshFilter filter in Filters)
                {
                    if (filter.sharedMesh == null || !Known.Add(filter))
                        continue;

                    MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                    string name = filter.gameObject.name;
                    WaterKind kind;

                    if (name == RiversName)
                    {
                        kind = WaterKind.River;
                        NewRivers.Add(renderer != null ? renderer.bounds : filter.sharedMesh.bounds);

                        if (_riverMaterial == null && renderer != null)
                            _riverMaterial = renderer.sharedMaterial;
                    }
                    else
                    {
                        // вид — на весь водоём по всем его загруженным кускам: кусок в тайле решал сам, и один водоём
                        // в соседних тайлах был болотом и озером с прямой границей по сетке
                        Body body = AddPiece(name, filter, root);

                        // у реки — речная вся: и куски этого водоёма в соседних тайлах, уже одетые озером
                        if (!body.Joined && JoinsRiver(filter))
                            body.Joined = true;

                        kind = KindOf(body);
                        Apply(name, kind);
                    }

                    var surface = new Surface
                    {
                        Filter = filter,
                        Kind = kind,
                        Bounds = renderer != null ? renderer.bounds : filter.sharedMesh.bounds,
                        Root = root
                    };

                    Surfaces.Add(surface);
                    Version++;

                    if (renderer != null)
                        Dress(renderer, surface.Kind);

                    if (surface.Kind == WaterKind.River)
                        BakeFlow(surface, root);
                }
            }

            JoinNewRivers();
        }

        // Реки новых тайлов: стоячая вода соседних тайлов, загруженная раньше них, их не видела — проверить её ещё раз,
        // иначе водоём у реки оставался озером или болотом, смотря какой тайл загрузился первым.
        private static void JoinNewRivers()
        {
            if (NewRivers.Count == 0)
                return;

            for (int i = 0; i < Surfaces.Count; i++)
            {
                Surface surface = Surfaces[i];

                if (surface.Filter == null || surface.Kind == WaterKind.River)
                    continue;

                string name = surface.Filter.gameObject.name;

                if (!Bodies.TryGetValue(name, out Body body) || body.Joined)
                    continue;

                bool near = false;
                Bounds bounds = surface.Bounds;

                foreach (Bounds river in NewRivers)
                {
                    if (bounds.max.x + JoinReach >= river.min.x && bounds.min.x - JoinReach <= river.max.x &&
                        bounds.max.z + JoinReach >= river.min.z && bounds.min.z - JoinReach <= river.max.z)
                    {
                        near = true;
                        break;
                    }
                }

                if (!near || !JoinsRiver(surface.Filter))
                    continue;

                body.Joined = true;
                Apply(name, KindOf(body));
            }

            NewRivers.Clear();
        }

        private static void Forget(int index)
        {
            Surface surface = Surfaces[index];
            Known.Remove(surface.Filter);

            if (surface.Owned != null)
                Object.Destroy(surface.Owned);

            Surfaces.RemoveAt(index);
            Version++;
        }

        private static void Dress(MeshRenderer renderer, WaterKind kind)
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // реки — с материалом пакета (Water (project))
            if (kind == WaterKind.River)
                return;

            Material material = MaterialOf(kind);

            if (material != null)
                renderer.sharedMaterial = material;
        }

        private static Material MaterialOf(WaterKind kind)
        {
            int index = (int)kind;

            if (!MaterialsLoaded[index])
            {
                MaterialsLoaded[index] = true;
                string address = kind switch
                {
                    WaterKind.Swamp => GeoAddressableAssets.SwampWater,
                    WaterKind.Sea => GeoAddressableAssets.SeaWater,
                    _ => GeoAddressableAssets.LakeWater
                };
                Materials[index] = GeoAddressableAssets.Load<Material>(address);

                if (Materials[index] == null)
                    Debug.LogWarning($"[WaterSurfaces] Water material is missing: {address}");
            }

            return Materials[index];
        }

        // Кусок водоёма name в тайле root: его замеры — к водоёму (кусок того же тайла, загруженный снова, — вместо
        // прежнего).
        private static Body AddPiece(string name, MeshFilter filter, GeoWorldRoot root)
        {
            if (!Bodies.TryGetValue(name, out Body body))
                Bodies[name] = body = new Body();

            long key = root.tile ? Key(root.tileIndex.x, root.tileIndex.y) : (1L << 62) | (uint)root.GetInstanceID();
            body.Pieces[key] = Measure(filter, root);
            return body;
        }

        // Вид водоёма по всем его загруженным кускам: море (большая открытая вода — и у реки: водохранилище остаётся
        // водохранилищем), речная (касается реки), болото (болото по карте земель на доле берега или внутри воды) или
        // пруд и озеро. Большая заросшая вода — болото, не море. Зависит только от того, какие куски загружены, не от
        // порядка: и река, которую водоём касается, может загрузиться после него.
        private static WaterKind KindOf(Body body)
        {
            var sum = new Piece();

            foreach (Piece piece in body.Pieces.Values)
            {
                sum.Area += piece.Area;
                sum.ShoreLand += piece.ShoreLand;
                sum.ShoreWetland += piece.ShoreWetland;
                sum.Inside += piece.Inside;
                sum.InsideWetland += piece.InsideWetland;
            }

            float shoreShare = sum.ShoreLand > 0f ? sum.ShoreWetland / sum.ShoreLand : 0f;
            float insideShare = sum.Inside > 0f ? sum.InsideWetland / sum.Inside : 0f;

            if (sum.Area >= SeaArea && insideShare < SwampShare)
                return WaterKind.Sea;

            if (body.Joined)
                return WaterKind.River;

            return Mathf.Max(shoreShare, insideShare) >= SwampShare ? WaterKind.Swamp : WaterKind.Lake;
        }

        // Водоём name стал другим видом (загрузился ещё кусок или река рядом) — переодеть его загруженные куски: речным —
        // материал рек пакета и поток, стоячим — свой материал.
        private static void Apply(string name, WaterKind kind)
        {
            foreach (Surface surface in Surfaces)
            {
                if (surface.Filter == null || surface.Kind == kind || surface.Filter.gameObject.name != name)
                    continue;

                surface.Kind = kind;
                Version++;

                if (!surface.Filter.TryGetComponent(out MeshRenderer renderer))
                    continue;

                if (kind != WaterKind.River)
                {
                    Dress(renderer, kind);
                    continue;
                }

                if (_riverMaterial != null)
                    renderer.sharedMaterial = _riverMaterial;

                if (surface.Owned == null && surface.Root != null)
                    BakeFlow(surface, surface.Root);
            }
        }

        // Замеры куска: площадь, берег (длина кромки по карте земель в ShoreReach м снаружи: суша и болото) и вода внутри
        // (площадь треугольников по карте земель под их центром: всего и болото). Разрез по границе тайла — не берег: за ним
        // тот же водоём в соседнем тайле, и у заросшей воды (болото по карте земель и в воде) кусок посреди неё считал
        // болотом весь свой «берег», а соседний с настоящим берегом — нет.
        private static Piece Measure(MeshFilter filter, GeoWorldRoot root)
        {
            var piece = new Piece();
            Mesh mesh = filter.sharedMesh;

            if (!mesh.isReadable)
                return piece;

            Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;

            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]);

            int[] triangles = mesh.triangles;

            for (int t = 0; t + 2 < triangles.Length; t += 3)
                piece.Area += TriangleArea(vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]]);

            Rect tile = root.terrain != null
                ? new Rect(root.terrain.transform.position.x, root.terrain.transform.position.z,
                    root.terrain.terrainData.size.x, root.terrain.terrainData.size.z)
                : root.tile ? root.tileArea : Rect.zero;
            List<(Vector3 middle, Vector2 outward, float length)> shore = ShoreEdges(vertices, triangles);
            int step = Mathf.Max(1, shore.Count / ShoreSamples);

            for (int i = 0; i < shore.Count; i += step)
            {
                (Vector3 middle, Vector2 outward, float length) = shore[i];

                if (OnTileEdge(middle, outward, tile))
                    continue;

                Vector3 point = middle + new Vector3(outward.x, 0f, outward.y) * ShoreReach;

                if (!GeoLandCover.TrySample(point, out GeoLandClass cover) || cover == GeoLandClass.Water)
                    continue;

                float weight = length * step;
                piece.ShoreLand += weight;

                if (cover == GeoLandClass.Wetland)
                    piece.ShoreWetland += weight;
            }

            // заросшая вода: карта земель внутри неё — болото
            int triangleStep = Mathf.Max(1, triangles.Length / 3 / ShoreSamples) * 3;

            for (int t = 0; t + 2 < triangles.Length; t += triangleStep)
            {
                Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];

                if (!GeoLandCover.TrySample((a + b + c) / 3f, out GeoLandClass cover))
                    continue;

                float weight = TriangleArea(a, b, c) * (triangleStep / 3);
                piece.Inside += weight;

                if (cover == GeoLandClass.Wetland)
                    piece.InsideWetland += weight;
            }

            return piece;
        }

        // Ребро кромки лежит на границе тайла (меш воды там разрезан): середина на линии, наружу — поперёк неё.
        private static bool OnTileEdge(Vector3 middle, Vector2 outward, Rect tile)
        {
            if (tile.width <= 0f)
                return false;

            const float tolerance = 0.05f;
            bool onX = Mathf.Abs(middle.x - tile.xMin) < tolerance || Mathf.Abs(middle.x - tile.xMax) < tolerance;
            bool onZ = Mathf.Abs(middle.z - tile.yMin) < tolerance || Mathf.Abs(middle.z - tile.yMax) < tolerance;
            return onX && Mathf.Abs(outward.x) > 0.99f || onZ && Mathf.Abs(outward.y) > 0.99f;
        }

        private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c) =>
            Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;

        // Кромка воды: рёбра меша, у которых один треугольник, — середина и направление наружу (от треугольника).
        private static List<(Vector3 middle, Vector2 outward, float length)> ShoreEdges(Vector3[] vertices, int[] triangles)
        {
            // ребро — по округлённым точкам: у меша вершины могут быть свои у каждого треугольника
            var edges = new Dictionary<(long, long), (int count, Vector3 a, Vector3 b, Vector3 opposite)>();

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    Vector3 a = vertices[triangles[t + e]];
                    Vector3 b = vertices[triangles[t + (e + 1) % 3]];
                    Vector3 opposite = vertices[triangles[t + (e + 2) % 3]];
                    long ka = PointKey(a), kb = PointKey(b);
                    (long, long) key = ka < kb ? (ka, kb) : (kb, ka);
                    edges[key] = edges.TryGetValue(key, out var known) ? (known.count + 1, a, b, opposite) : (1, a, b, opposite);
                }
            }

            var shore = new List<(Vector3 middle, Vector2 outward, float length)>();

            foreach (var edge in edges.Values)
            {
                if (edge.count != 1)
                    continue;

                Vector3 middle = (edge.a + edge.b) * 0.5f;
                var along = new Vector2(edge.b.x - edge.a.x, edge.b.z - edge.a.z);
                var outward = new Vector2(along.y, -along.x).normalized;

                if (Vector2.Dot(outward, new Vector2(edge.opposite.x - middle.x, edge.opposite.z - middle.z)) > 0f)
                    outward = -outward;

                shore.Add((middle, outward, along.magnitude));
            }

            return shore;
        }

        // Стоячая вода касается реки: за её кромкой в JoinReach м — меш реки, на JoinLength м кромки и больше.
        private static bool JoinsRiver(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;

            if (!mesh.isReadable)
                return false;

            Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;

            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]);

            float touching = 0f;

            foreach ((Vector3 middle, Vector2 outward, float length) in ShoreEdges(vertices, mesh.triangles))
            {
                if (!RiverAt(middle + new Vector3(outward.x, 0f, outward.y) * JoinReach))
                    continue;

                touching += length;

                if (touching >= JoinLength)
                    return true;
            }

            return false;
        }

        private static bool RiverAt(Vector3 point)
        {
            foreach (Surface surface in Surfaces)
            {
                if (surface.Filter == null || surface.Filter.gameObject.name != RiversName)
                    continue;

                Bounds bounds = surface.Bounds;

                if (point.x < bounds.min.x || point.x > bounds.max.x || point.z < bounds.min.z || point.z > bounds.max.z)
                    continue;

                if (TryGetSurfaceHeight(surface, point, out _))
                    return true;
            }

            return false;
        }

        private static long PointKey(Vector3 point) =>
            ((long)Mathf.RoundToInt(point.x * 100f) << 32) ^ (uint)Mathf.RoundToInt(point.z * 100f);

        private struct FlowSegment
        {
            public Vector2 A, B, Direction, Previous, Next;
            public float Length, Width;
        }

        // Поток реки в UV3 своей копии меша (м/с, мир xz): по ближайшей оси реки тайла, вниз по течению; скорость на
        // стрежне по ширине (ручей быстрее широкой реки), у берегов меньше, за берегом сходит на нет.
        private static void BakeFlow(Surface surface, GeoWorldRoot root)
        {
            MeshFilter filter = surface.Filter;
            Mesh mesh = filter.sharedMesh;

            if (mesh == null || !mesh.isReadable)
                return;

            Paths.Clear();
            root.GetComponentsInChildren(true, Paths);
            var segments = new List<FlowSegment>();

            foreach (GeoPath path in Paths)
            {
                if (!path.river || path.points == null || path.points.Count < 2)
                    continue;

                // линии рек OSM идут по течению, GeoWorld их порядок сохраняет (по высоте воды направление не узнать:
                // она идёт по рельефу и скачет на ±1 м там, где река падает на 0.1 м за километр)
                List<Vector3> points = path.points;
                int start = segments.Count;

                for (int i = 0; i + 1 < points.Count; i++)
                {
                    var a = new Vector2(points[i].x, points[i].z);
                    var b = new Vector2(points[i + 1].x, points[i + 1].z);
                    float length = Vector2.Distance(a, b);

                    if (length < 0.01f)
                        continue;

                    Vector2 direction = (b - a) / length;
                    segments.Add(new FlowSegment { A = a, B = b, Direction = direction, Previous = direction, Next = direction, Length = length, Width = path.width });
                }

                // направления соседних отрезков: на изгибе поток поворачивает плавно
                for (int i = start; i < segments.Count; i++)
                {
                    FlowSegment segment = segments[i];

                    if (i > start)
                        segment.Previous = segments[i - 1].Direction;

                    if (i + 1 < segments.Count)
                        segment.Next = segments[i + 1].Direction;

                    segments[i] = segment;
                }
            }

            if (segments.Count == 0)
                return;

            var grid = new Dictionary<long, List<int>>();

            for (int i = 0; i < segments.Count; i++)
            {
                FlowSegment segment = segments[i];
                float reach = segment.Width * 0.5f + FlowReach;
                int minX = Mathf.FloorToInt((Mathf.Min(segment.A.x, segment.B.x) - reach) / FlowCellSize);
                int maxX = Mathf.FloorToInt((Mathf.Max(segment.A.x, segment.B.x) + reach) / FlowCellSize);
                int minZ = Mathf.FloorToInt((Mathf.Min(segment.A.y, segment.B.y) - reach) / FlowCellSize);
                int maxZ = Mathf.FloorToInt((Mathf.Max(segment.A.y, segment.B.y) + reach) / FlowCellSize);

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        long key = Key(x, z);

                        if (!grid.TryGetValue(key, out List<int> list))
                            grid[key] = list = new List<int>(8);

                        list.Add(i);
                    }
                }
            }

            Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;
            var flows = new Vector2[vertices.Length];
            // UV2: сколько метров от оси реки (со знаком: + слева по течению) и полуширина реки — полосы и затишье
            // вдоль берегов (Game/Water/River)
            var banks = new Vector2[vertices.Length];

            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 world = toWorld.MultiplyPoint3x4(vertices[v]);
                var point = new Vector2(world.x, world.z);

                if (!grid.TryGetValue(Key(Mathf.FloorToInt(point.x / FlowCellSize), Mathf.FloorToInt(point.y / FlowCellSize)), out List<int> near))
                    continue;

                float best = float.PositiveInfinity;
                Vector2 flow = Vector2.zero;
                Vector2 bank = Vector2.zero;

                foreach (int index in near)
                {
                    FlowSegment segment = segments[index];
                    float along = Mathf.Clamp01(Vector2.Dot(point - segment.A, segment.Direction) / segment.Length);
                    float distance = Vector2.Distance(point, segment.A + (segment.B - segment.A) * along);
                    float halfWidth = Mathf.Max(segment.Width * 0.5f, 2f);

                    // ближе к оси — та ось и решает (по краю её ширины: узкий приток у широкой реки не перебивает её)
                    float score = distance / halfWidth;

                    if (score >= best)
                        continue;

                    best = score;
                    Vector2 tangent = (segment.Direction + segment.Previous * Mathf.Max(0f, 0.5f - along) + segment.Next * Mathf.Max(0f, along - 0.5f)).normalized;
                    float centre = Mathf.Clamp(0.35f + 4f / Mathf.Max(segment.Width, 2f), 0.4f, 1.2f);
                    float across = Mathf.Clamp01(distance / halfWidth);
                    float fade = 1f - Mathf.Clamp01((distance - halfWidth) / FlowReach);
                    flow = tangent * (centre * (0.35f + 0.65f * (1f - across * across)) * fade);
                    Vector2 offset = point - segment.A;
                    float side = segment.Direction.x * offset.y - segment.Direction.y * offset.x;
                    bank = new Vector2(side < 0f ? -distance : distance, halfWidth);
                }

                flows[v] = flow;
                banks[v] = bank;
            }

            Mesh copy = Object.Instantiate(mesh);
            copy.name = mesh.name + " (flow)";
            copy.SetUVs(3, flows);
            copy.SetUVs(2, banks);
            filter.sharedMesh = copy;
            surface.Owned = copy;
        }

        private static bool TryGetSurfaceHeight(Surface surface, Vector3 position, out float height)
        {
            height = 0f;

            if (surface.Cells == null && !Build(surface))
                return false;

            long key = Key(Mathf.FloorToInt(position.x / CellSize), Mathf.FloorToInt(position.z / CellSize));

            if (!surface.Cells.TryGetValue(key, out List<int> triangles))
                return false;

            Vector3[] vertices = surface.Vertices;
            int[] indices = surface.Triangles;
            bool found = false;

            foreach (int triangle in triangles)
            {
                Vector3 a = vertices[indices[triangle]];
                Vector3 b = vertices[indices[triangle + 1]];
                Vector3 c = vertices[indices[triangle + 2]];

                if (!Barycentric(position.x, position.z, a, b, c, out float u, out float v, out float w))
                    continue;

                float y = a.y * u + b.y * v + c.y * w;
                height = found ? Mathf.Max(height, y) : y;
                found = true;
            }

            return found;
        }

        // Треугольники меша в мире по ячейкам CellSize: запрос смотрит только свою ячейку.
        private static bool Build(Surface surface)
        {
            Mesh mesh = surface.Filter.sharedMesh;

            if (mesh == null || !mesh.isReadable)
            {
                surface.Cells = new Dictionary<long, List<int>>();
                surface.Vertices = System.Array.Empty<Vector3>();
                surface.Triangles = System.Array.Empty<int>();
                return false;
            }

            Matrix4x4 toWorld = surface.Filter.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;

            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]);

            int[] indices = mesh.triangles;
            Dictionary<long, List<int>> cells = new();

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                Vector3 a = vertices[indices[t]];
                Vector3 b = vertices[indices[t + 1]];
                Vector3 c = vertices[indices[t + 2]];
                int minX = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / CellSize);
                int maxX = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / CellSize);
                int minZ = Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)) / CellSize);
                int maxZ = Mathf.FloorToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)) / CellSize);

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        long key = Key(x, z);

                        if (!cells.TryGetValue(key, out List<int> list))
                            cells[key] = list = new List<int>(4);

                        list.Add(t);
                    }
                }
            }

            surface.Vertices = vertices;
            surface.Triangles = indices;
            surface.Cells = cells;
            return true;
        }

        private static bool Barycentric(float x, float z, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
        {
            float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            u = v = w = 0f;

            if (Mathf.Abs(denominator) < 1e-6f)
                return false;

            u = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / denominator;
            v = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / denominator;
            w = 1f - u - v;
            const float epsilon = -1e-4f;
            return u >= epsilon && v >= epsilon && w >= epsilon;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            Surfaces.Clear();
            Known.Clear();
            Bodies.Clear();
            NewRivers.Clear();
            _riverMaterial = null;
            _nextScan = float.NegativeInfinity;
            System.Array.Clear(Materials, 0, Materials.Length);
            System.Array.Clear(MaterialsLoaded, 0, MaterialsLoaded.Length);
        }
    }
}
