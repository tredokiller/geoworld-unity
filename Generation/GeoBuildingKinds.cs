using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The export guesses the kind of a bare building=yes or a Microsoft outline from its size alone: under 35 m² a shed,
    // under 300 m² a house, a long one 9 to 18 m wide a block of flats, anything else industrial. On Bakhmut's battlefield
    // that is 20 400 of 21 400 buildings, and the models drew what the guess said: 290 houses, 110 sheds and 40 blocks of
    // flats in industrial estates, 160 houses, 50 sheds and 20 blocks in garage cooperatives, production halls for tower
    // blocks. For a guessed building Kinds looks at what the export says about it and around it, and gives it another kind
    // where that says more, in this order:
    // - its name: a school, a kindergarten, a hospital, a shop, a church, a garage (with the OSM tag their models are kept
    //   for);
    // - three storeys or more (building:levels or a height): a block of flats;
    // - the land use it stands in: garages, industrial, retail or commercial, allotments, a farmyard, education, religious;
    // - a strip 5 to 7.5 m deep and 15 m long or more: a row of garages (a barn with a summer kitchen in a village's yards);
    // - a compact building of 300 to 1500 m² among blocks of flats (two within 100 m): a tower block, not a hall;
    // - a small house among blocks of flats with few houses around (two blocks within 60 m, fewer than three houses within
    //   50 m): a courtyard's boiler house, shop or garages.
    // Mapped kinds stay as they are. Only the export decides, the same in every tile.
    public static class GeoBuildingKinds
    {
        public const string Source = "inferred";

        public struct Kind
        {
            public string category, tag;
        }

        static readonly (string[] words, string category, string tag)[] Names =
        {
            (new[] { "дитячий садок", "дитсадок", "садок", "ясла", "детский сад", "детсад", "kindergarten" }, "public", "kindergarten"),
            (new[] { "школа", "школ", "гімназ", "гимназ", "ліцей", "лицей", "school" }, "public", "school"),
            (new[] { "лікарн", "больниц", "полікліні", "поликлини", "амбулатор", "hospital" }, "public", "hospital"),
            (new[] { "церква", "церков", "храм", "собор", "монастир", "church" }, "religious", "church"),
            (new[] { "магазин", "атб", "сільпо", "сильпо", "супермаркет", "маркет", "market", "ринок", "рынок", "торгов", "shop" }, "commercial", "retail"),
            (new[] { "гараж", "гск", "garage" }, "garage", "garages"),
        };

        // The kinds of the guessed buildings that something says more about, by ID.
        public static Dictionary<string, Kind> For(ICollection<BuildingFeature> buildings, IList<AreaFeature> areas)
        {
            var result = new Dictionary<string, Kind>();
            if (buildings == null) return result;
            var landUse = new LandUse(areas);
            var all = new List<Entry>();
            foreach (var b in buildings)
            {
                if (b == null || string.IsNullOrEmpty(b.id) || !GeoWorldBuild.ValidBuilding(b)) continue;
                float width = Mathf.Min(b.width, b.length), length = Mathf.Max(b.width, b.length);
                all.Add(new Entry
                {
                    b = b, centre = new Vector2(b.x, b.z), area = Mathf.Abs(GeoPolygon.Area(GeoHomeSites.Outline(b))), width = width, length = length,
                    guessed = Guessed(b), category = b.category
                });
            }
            // what the building itself and its land use say
            foreach (var e in all)
            {
                if (!e.guessed) continue;
                e.use = landUse.At(e.centre);
                var kind = Own(e, e.use);
                if (kind.category == null) continue;
                e.category = kind.category;
                result[e.b.id] = kind;
            }
            // what the blocks of flats around say (with the kinds found above)
            const float Cell = 50;
            var grid = new Dictionary<Vector2Int, List<Entry>>();
            foreach (var e in all)
            {
                if (e.category != "apartments" && e.category != "house") continue;
                var key = new Vector2Int(Mathf.FloorToInt(e.centre.x / Cell), Mathf.FloorToInt(e.centre.y / Cell));
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<Entry>();
                list.Add(e);
            }
            int Count(Entry e, string category, float radius)
            {
                int n = 0, cx = Mathf.FloorToInt(e.centre.x / Cell), cz = Mathf.FloorToInt(e.centre.y / Cell), r = Mathf.CeilToInt(radius / Cell);
                for (int x = cx - r; x <= cx + r; x++)
                    for (int z = cz - r; z <= cz + r; z++)
                    {
                        if (!grid.TryGetValue(new Vector2Int(x, z), out var list)) continue;
                        foreach (var other in list)
                            if (other != e && other.category == category && (other.centre - e.centre).sqrMagnitude <= radius * radius) n++;
                    }
                return n;
            }
            var changes = new List<(Entry e, string category)>();
            foreach (var e in all)
            {
                // not where the land use has said what stands there
                if (!e.guessed || result.ContainsKey(e.b.id) || (e.use != null && e.use != "landuse=residential")) continue;
                if (e.category == "industrial" && e.area >= 300 && e.area <= 1500 && e.length <= 1.8f * e.width && Count(e, "apartments", 100) >= 2)
                    changes.Add((e, "apartments"));
                else if (e.category == "house" && Count(e, "apartments", 60) >= 2 && Count(e, "house", 50) < 3)
                    changes.Add((e, "utility"));
            }
            foreach (var (e, category) in changes)
            {
                e.category = category;
                result[e.b.id] = new Kind { category = category };
            }
            return result;
        }

        // A copy of a building with the kind found for it (its OSM tag too when the kind has one).
        public static BuildingFeature Apply(BuildingFeature b, Kind kind)
        {
            var copy = b.Clone();
            copy.category = kind.category; copy.categorySource = Source;
            if (!string.IsNullOrEmpty(kind.tag)) copy.buildingTag = kind.tag;
            return copy;
        }

        // what the building's name, storeys, shape and land use say, or no kind
        static Kind Own(Entry e, string use)
        {
            var b = e.b;
            if (!string.IsNullOrEmpty(b.name))
            {
                string name = b.name.ToLowerInvariant();
                foreach (var (words, category, tag) in Names)
                    foreach (var word in words)
                        if (name.Contains(word)) return Change(e, category, tag);
            }
            bool levels = b.heightSource == "levels" || b.heightSource == "tag";
            switch (use)
            {
                case "landuse=garages": return Change(e, "garage", null);
                case "landuse=industrial": case "landuse=railway": case "landuse=quarry": case "landuse=depot":
                    return e.area >= 35 ? Change(e, "industrial", null) : default;
                case "landuse=retail": case "landuse=commercial":
                    return e.area >= 35 ? Change(e, "commercial", null) : default;
                case "landuse=allotments":
                    return e.category == "house" && e.area < 80 ? Change(e, "shed", null) : default;
                case "landuse=farmyard": case "landuse=farmland": case "landuse=greenhouse_horticulture":
                    return e.area >= 150 ? Change(e, "farm", null) : default;
                case "landuse=education": case "amenity=school": case "amenity=kindergarten": case "amenity=college": case "amenity=university":
                    if (e.area >= 300) return Change(e, "public", use == "amenity=kindergarten" ? "kindergarten" : "school");
                    break;
                case "landuse=religious": case "amenity=place_of_worship":
                    if (e.area >= 100) return Change(e, "religious", null);
                    break;
                case "amenity=hospital": case "amenity=clinic":
                    if (e.area >= 300) return Change(e, "public", "hospital");
                    break;
            }
            if (levels && b.levels >= 3) return Change(e, "apartments", null);
            // a long narrow building: a row of garages, or in a village's yards a barn and summer kitchen under one roof
            if (e.area >= 35 && e.width >= 5 && e.width <= 7.5f && e.length >= 15 && e.length >= 2.5f * e.width)
                return Change(e, use == "landuse=residential" ? "shed" : "garage", null);
            return default;
        }

        static Kind Change(Entry e, string category, string tag) =>
            category == e.category && string.IsNullOrEmpty(tag) ? default : new Kind { category = category, tag = tag };

        // a category found from the outline's size, not mapped (older exports say nothing: a bare building=yes is guessed)
        internal static bool Guessed(BuildingFeature b) =>
            b.categorySource != null ? b.categorySource == "guessed" : string.IsNullOrEmpty(b.buildingTag) || b.buildingTag == "yes";

        sealed class Entry
        {
            public BuildingFeature b;
            public Vector2 centre;
            public float area, width, length;
            public bool guessed;
            public string category, use;
        }

        // The land use areas of the export (landuse=*, and the amenities that mark a school, a hospital or a church's
        // grounds), the smallest that holds a point winning: a garage cooperative inside a residential area is garages.
        sealed class LandUse
        {
            readonly List<(string tag, float area, Rect box, Vector2[] ring)> polygons = new List<(string, float, Rect, Vector2[])>();

            public LandUse(IList<AreaFeature> areas)
            {
                if (areas == null) return;
                foreach (var a in areas)
                {
                    if (a?.tag == null || a.rings == null) continue;
                    if (!a.tag.StartsWith("landuse=", StringComparison.Ordinal) && !a.tag.StartsWith("amenity=", StringComparison.Ordinal)) continue;
                    foreach (var flat in a.rings)
                    {
                        if (flat == null || flat.Length < 6) continue;
                        var ring = new Vector2[flat.Length / 2];
                        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                        for (int i = 0; i < ring.Length; i++)
                        {
                            ring[i] = new Vector2(flat[2 * i], flat[2 * i + 1]);
                            minX = Mathf.Min(minX, ring[i].x); maxX = Mathf.Max(maxX, ring[i].x); minZ = Mathf.Min(minZ, ring[i].y); maxZ = Mathf.Max(maxZ, ring[i].y);
                        }
                        polygons.Add((a.tag, Mathf.Abs(GeoPolygon.Area(ring)), Rect.MinMaxRect(minX, minZ, maxX, maxZ), ring));
                    }
                }
                polygons.Sort((x, y) => x.area.CompareTo(y.area));
            }

            public string At(Vector2 point)
            {
                foreach (var p in polygons)
                    if (p.box.Contains(point) && GeoPolygon.Contains(p.ring, point)) return p.tag;
                return null;
            }
        }
    }
}
