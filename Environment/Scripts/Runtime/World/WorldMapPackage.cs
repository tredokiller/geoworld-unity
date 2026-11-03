using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using GeoWorld;
using GeoWorld.Editor;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Данные карты мира (дороги, поля, контуры лесов с типом листвы) в пакете мира GeoWorld — мире, который генерирует плеер
    /// (GeoWorldGenerator). При генерации печёт их из выгрузки (WorldMapExport) — с шагом по снимку, как редактор: пока
    /// генератор строит тайл (GeoWorldGenerator.TileBuilt), снимок его террейна читается (SatelliteImage), и оси дорог
    /// подтягиваются к дороге на нём (RoadSatelliteSnap), а рядки полей берут направление его рисунка (FieldRowsSatellite);
    /// бордюры — по карте земель пакета. Всё — в extra «world map» (worldmap.json.gz); при открытии пакета
    /// (GeoWorldPackage.Open, его делает GeoWorldPackageStreamer) ставит на хост пакета WorldMapData с ними, при закрытии
    /// убирает. Так у мира плеера покрытие дорог, трава мимо дорог, рядки полей и породы по типу листвы — как у мира из
    /// редактора.
    /// </summary>
    public static class WorldMapPackage
    {
        public const string Name = "world map";
        public const string FileName = "worldmap.json.gz";

        // одна генерация за раз (GeoWorldGenerator): её дороги и поля со снимками тайлов между Starting и Baking
        private sealed class State
        {
            public GeoPackageBake Bake;
            public List<RoadAreas.Road> Roads;
            public List<Rect> Coverage;
            public RoadJunctions.Links Links;
            public string Summary;
            public RoadSatelliteSnap.Job Snap;
            public List<FieldAreas.Field> Fields;
            public FieldRowsSatellite.Job Rows;
            public SatelliteImage.Reader RoadReader;
            public SatelliteImage.Reader FieldReader;
            public int Tiles;
        }

        private static State _state;

        [Serializable]
        private sealed class Payload
        {
            public RoadAreas.Road[] roads = Array.Empty<RoadAreas.Road>();
            public Rect[] coverage = Array.Empty<Rect>();
            public FieldAreas.Field[] fields = Array.Empty<FieldAreas.Field>();
            public TreeSpeciesProfile.LeafArea[] leafAreas = Array.Empty<TreeSpeciesProfile.LeafArea>();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            GeoWorldGenerator.Starting -= Start;
            GeoWorldGenerator.Starting += Start;
            GeoWorldGenerator.TileBuilt -= Tile;
            GeoWorldGenerator.TileBuilt += Tile;
            GeoWorldGenerator.Baking -= Bake;
            GeoWorldGenerator.Baking += Bake;
            GeoWorldPackage.Opened -= Open;
            GeoWorldPackage.Opened += Open;
            GeoWorldPackage.Closed -= Close;
            GeoWorldPackage.Closed += Close;
        }

        // Дороги и поля выгрузки — до тайлов: их снимки придут по тайлу.
        private static void Start(GeoPackageBake bake)
        {
            Dispose();
            var exports = new[] { bake.Environment };
            State state = new() { Bake = bake, Coverage = new List<Rect>() };
            state.Roads = WorldMapExport.Roads(exports, state.Coverage, out state.Links, out state.Summary);
            state.Snap = RoadSatelliteSnap.Begin(state.Roads, Vector2.zero, bake.TileSize);
            state.Fields = WorldMapExport.Fields(exports);
            state.Rows = FieldRowsSatellite.Begin(state.Fields);
            _state = state;
        }

        // Снимок построенного тайла — дорогам на нём и полям на нём.
        private static void Tile(GeoPackageBake bake, Vector2Int index, GameObject root)
        {
            State state = _state;

            if (state == null || state.Bake != bake || root == null)
                return;

            Terrain terrain = root.GetComponentInChildren<Terrain>(true);
            Texture satellite = terrain != null ? SatelliteImage.Of(terrain.terrainData) : null;

            if (satellite == null)
                return;

            GeoBounds core = GeoTilePlan.Tile(index, bake.TileSize);
            Rect area = new(core.minX, core.minZ, core.Width, core.Depth);
            bool read = false;

            if (state.Snap.Wants(index))
            {
                state.RoadReader ??= new SatelliteImage.Reader(RoadSatelliteSnap.SatelliteSize, false);
                RoadSatelliteSnap.AddTile(state.Snap, index, area, state.RoadReader.Read(satellite));
                read = true;
            }

            if (state.Rows.Wants(area))
            {
                state.FieldReader ??= new SatelliteImage.Reader(FieldRowsSatellite.SatelliteSize, true);
                FieldRowsSatellite.AddTile(state.Rows, area, state.FieldReader.Read(satellite));
                read = true;
            }

            if (read)
                state.Tiles++;
        }

        private static void Bake(GeoPackageBake bake)
        {
            var exports = new[] { bake.Environment };
            State state = _state != null && _state.Bake == bake ? _state : null;
            List<Rect> coverage = state?.Coverage ?? new List<Rect>();
            List<RoadAreas.Road> roads;
            string summary, snapped = "no satellite";
            List<FieldAreas.Field> fields;
            int oriented = 0;

            try
            {
                if (state != null)
                {
                    roads = state.Roads;
                    summary = state.Summary;
                    // как редактор: сначала оси и ширина по снимку, потом бордюры (по ширине город отличается от села)
                    snapped = RoadSatelliteSnap.Finish(state.Snap, state.Links) + $" ({state.Tiles} tiles read)";
                    fields = state.Fields;
                    oriented = FieldRowsSatellite.Finish(state.Rows);
                }
                else
                {
                    roads = WorldMapExport.Roads(exports, coverage, out _, out summary);
                    fields = WorldMapExport.Fields(exports);
                }
            }
            finally
            {
                Dispose();
            }

            GeoGroundMap ground = bake.Ground;
            int curbs = WorldMapExport.MarkCurbs(roads, ground != null ? ground.At : null);
            Payload payload = new()
            {
                roads = roads.ToArray(), coverage = coverage.ToArray(),
                fields = fields.ToArray(), leafAreas = WorldMapExport.LeafAreas(exports).ToArray()
            };
            bake.Add(Name, FileName, Compress(JsonUtility.ToJson(payload)));
            bake.Notes.Add($"World map data: {summary}, {curbs} with curbs; {snapped}; {payload.fields.Length} fields, rows from the satellite on {oriented}; " +
                           $"{payload.leafAreas.Length} leaf areas.");
        }

        private static void Dispose()
        {
            if (_state == null)
                return;

            _state.RoadReader?.Dispose();
            _state.FieldReader?.Dispose();
            _state = null;
        }

        private static void Open(GeoWorldPackage package, GameObject host)
        {
            byte[] bytes = package.ReadExtra(Name);

            if (bytes == null)
                return;

            Payload payload = JsonUtility.FromJson<Payload>(Decompress(bytes));
            RoadAreas roads = Make<RoadAreas>("Road areas");
            roads.roads = payload.roads ?? Array.Empty<RoadAreas.Road>();
            roads.coverage = payload.coverage ?? Array.Empty<Rect>();
            FieldAreas fields = Make<FieldAreas>("Field areas");
            fields.fields = payload.fields ?? Array.Empty<FieldAreas.Field>();
            TreeLeafAreas leafAreas = Make<TreeLeafAreas>("Tree leaf areas");
            leafAreas.areas = payload.leafAreas ?? Array.Empty<TreeSpeciesProfile.LeafArea>();

            WorldMapData data = host.GetComponent<WorldMapData>();

            if (data == null)
                data = host.AddComponent<WorldMapData>();

            data.roads = roads;
            data.fields = fields;
            data.leafAreas = leafAreas;
            data.report = $"From the package: {roads.roads.Length} roads, {fields.fields.Length} fields, {leafAreas.areas.Length} leaf areas";
        }

        private static void Close(GeoWorldPackage package, GameObject host)
        {
            WorldMapData data = host != null ? host.GetComponent<WorldMapData>() : null;

            if (data == null)
                return;

            UnityEngine.Object.Destroy(data.roads);
            UnityEngine.Object.Destroy(data.fields);
            UnityEngine.Object.Destroy(data.leafAreas);
            UnityEngine.Object.Destroy(data);
        }

        private static T Make<T>(string name) where T : ScriptableObject
        {
            T asset = ScriptableObject.CreateInstance<T>();
            asset.name = name;
            asset.hideFlags = HideFlags.DontSave;
            return asset;
        }

        private static byte[] Compress(string text)
        {
            using MemoryStream stream = new();

            using (GZipStream zip = new(stream, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                zip.Write(bytes, 0, bytes.Length);
            }

            return stream.ToArray();
        }

        private static string Decompress(byte[] bytes)
        {
            using GZipStream zip = new(new MemoryStream(bytes), CompressionMode.Decompress);
            using StreamReader reader = new(zip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
