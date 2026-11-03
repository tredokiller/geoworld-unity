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
    /// (GeoWorldGenerator). При генерации печёт их из выгрузки (WorldMapExport; бордюры — по карте земель пакета) в extra
    /// «world map» (worldmap.json.gz), при открытии пакета (GeoWorldPackage.Open, его делает GeoWorldPackageStreamer)
    /// ставит на хост пакета WorldMapData с ними, при закрытии убирает. Так у мира плеера покрытие дорог, трава мимо
    /// дорог, рядки полей и породы по типу листвы — как у мира из редактора (кроме подтяжки к снимку: оси как в OSM).
    /// </summary>
    public static class WorldMapPackage
    {
        public const string Name = "world map";
        public const string FileName = "worldmap.json.gz";

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
            GeoWorldGenerator.Baking -= Bake;
            GeoWorldGenerator.Baking += Bake;
            GeoWorldPackage.Opened -= Open;
            GeoWorldPackage.Opened += Open;
            GeoWorldPackage.Closed -= Close;
            GeoWorldPackage.Closed += Close;
        }

        private static void Bake(GeoPackageBake bake)
        {
            var exports = new[] { bake.Environment };
            List<Rect> coverage = new();
            List<RoadAreas.Road> roads = WorldMapExport.Roads(exports, coverage, out string summary);
            GeoGroundMap ground = bake.Ground;
            int curbs = WorldMapExport.MarkCurbs(roads, ground != null ? ground.At : null);
            Payload payload = new()
            {
                roads = roads.ToArray(), coverage = coverage.ToArray(),
                fields = WorldMapExport.Fields(exports).ToArray(), leafAreas = WorldMapExport.LeafAreas(exports).ToArray()
            };
            bake.Add(Name, FileName, Compress(JsonUtility.ToJson(payload)));
            bake.Notes.Add($"World map data: {summary}, {curbs} with curbs; {payload.fields.Length} fields; {payload.leafAreas.Length} leaf areas.");
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
