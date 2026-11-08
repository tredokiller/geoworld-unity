using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // What stands around the buildings a generator draws with models: the other buildings' outlines (a model must not
    // reach into them) and the streets their fronts turn to. Only the outlines are known to every tile that sees a
    // building, so a model is checked against its neighbours' outlines, never against the models drawn on them: the choice
    // depends on the building and its neighbourhood alone, the same on both sides of a tile's edge.
    public sealed class GeoHomeSite
    {
        // world X/Z -> the unit direction to the nearest street within reach (GeoStreets: roads people live along, not
        // footways, paths or field tracks), or null
        public Func<Vector2, Vector2?> street;
        // world X/Z -> how far the street street() points to is (a long side nearer a street faces it), or null
        public Func<Vector2, float> streetDistance;
        // the kinds found for buildings the export guessed by their size (GeoBuildingKinds): a category and the OSM tag its
        // models are kept for, by ID
        public readonly Dictionary<string, (string category, string tag)> kinds = new Dictionary<string, (string, string)>();
        // the buildings drawn as a yard's outbuildings although the export calls them houses (GeoHomeSites.Outbuildings)
        public readonly HashSet<string> outbuildings = new HashSet<string>();

        const float Cell = 32;
        readonly Dictionary<Vector2Int, List<int>> cells = new Dictionary<Vector2Int, List<int>>();
        readonly List<string> ids = new List<string>();
        readonly List<Vector2[]> outlines = new List<Vector2[]>();
        readonly List<Rect> boxes = new List<Rect>();

        public int Count => outlines.Count;

        public bool IsOutbuilding(string id) => id != null && outbuildings.Contains(id);

        // An outline in world X/Z of the building with this ID.
        public void Add(string id, Vector2[] world)
        {
            if (world == null || world.Length < 3) return;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var p in world)
            {
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y)) return;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
            }
            if (maxX - minX > 1000 || maxZ - minZ > 1000) return;
            int index = outlines.Count;
            ids.Add(id ?? ""); outlines.Add(world); boxes.Add(Rect.MinMaxRect(minX, minZ, maxX, maxZ));
            for (int x = Mathf.FloorToInt(minX / Cell); x <= Mathf.FloorToInt(maxX / Cell); x++)
                for (int z = Mathf.FloorToInt(minZ / Cell); z <= Mathf.FloorToInt(maxZ / Cell); z++)
                {
                    var key = new Vector2Int(x, z);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                    list.Add(index);
                }
        }

        // The outlines of the other buildings whose box meets a world box, each once (the building's own ID left out).
        public void Near(Rect world, string except, List<Vector2[]> result)
        {
            result.Clear();
            if (outlines.Count == 0) return;
            var seen = new HashSet<int>();
            for (int x = Mathf.FloorToInt(world.xMin / Cell); x <= Mathf.FloorToInt(world.xMax / Cell); x++)
                for (int z = Mathf.FloorToInt(world.yMin / Cell); z <= Mathf.FloorToInt(world.yMax / Cell); z++)
                {
                    if (!cells.TryGetValue(new Vector2Int(x, z), out var list)) continue;
                    foreach (int i in list)
                        if (seen.Add(i) && boxes[i].Overlaps(world) && ids[i] != (except ?? "\u0001")) result.Add(outlines[i]);
                }
        }
    }
}
