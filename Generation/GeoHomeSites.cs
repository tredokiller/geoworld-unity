using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The site the generator gives home models (GeoHomeSite): the outline of every building it is given, those around the
    // area it builds too (a model at the edge of a tile must keep out of the house across the line, which the next tile
    // builds), the streets their fronts turn to, and which "houses" are a yard's outbuildings.
    public static class GeoHomeSites
    {
        public static GeoHomeSite For(ICollection<BuildingFeature> buildings, GeoStreets streets, GenerationSettings settings)
        {
            var site = new GeoHomeSite();
            if (streets != null && !streets.Empty) site.street = p => streets.Toward(p);
            if (buildings == null) return site;
            foreach (var b in buildings)
            {
                if (!GeoWorldBuild.ValidBuilding(b)) continue;
                site.Add(b.id, Outline(b));
            }
            if (settings == null || settings.yardOutbuildings) site.outbuildings.UnionWith(Outbuildings(buildings, streets));
            return site;
        }

        // A building's outline in world X/Z, as GeoBuildingBuilder.Import reads it (its box when it has no outline): the
        // export's offsets are east/north from its centre already.
        public static Vector2[] Outline(BuildingFeature b)
        {
            if (b.footprint != null && b.footprint.Length >= 6 && b.footprint.Length % 2 == 0)
            {
                var outline = new Vector2[b.footprint.Length / 2];
                for (int i = 0; i < outline.Length; i++) outline[i] = new Vector2(b.x + b.footprint[2 * i], b.z + b.footprint[2 * i + 1]);
                return outline;
            }
            var turn = Quaternion.Euler(0, b.rotation, 0);
            float w = Mathf.Max(0.5f, b.width) / 2, l = Mathf.Max(0.5f, b.length) / 2;
            var corners = new[] { new Vector3(-w, 0, -l), new Vector3(w, 0, -l), new Vector3(w, 0, l), new Vector3(-w, 0, l) };
            var box = new Vector2[4];
            for (int i = 0; i < 4; i++) { var p = turn * corners[i]; box[i] = new Vector2(b.x + p.x, b.z + p.z); }
            return box;
        }

        // The export calls a Microsoft outline or a bare building=yes a house by its size alone (35 to 300 m²), so a
        // village's summer kitchens, barns and garages were drawn as houses with a porch to the street: in the test villages
        // 44 % of those "houses" stood within 25 m of a house 1.3 times their size. Such a guessed house is a yard's
        // outbuilding when a house at least 1.2 times its size (and 45 m²) stands within 30 m and 3 m or more nearer the
        // street; with no street within 80 m when one 1.5 times its size stands there; or when it is under 45 m² with a house
        // 1.6 times its size within 15 m. That is 28 % of the guessed houses there (their median 50 m², the houses' 87 m²).
        // Houses over 160 m² and mapped ones stay houses. Only the outlines and the streets decide, the same in every tile.
        public static HashSet<string> Outbuildings(ICollection<BuildingFeature> buildings, GeoStreets streets)
        {
            var result = new HashSet<string>();
            if (buildings == null) return result;
            const float Cell = 30, Reach = 80;
            var houses = new List<House>();
            var grid = new Dictionary<Vector2Int, List<House>>();
            foreach (var b in buildings)
            {
                if (b == null || b.category != "house" || string.IsNullOrEmpty(b.id) || !GeoWorldBuild.ValidBuilding(b)) continue;
                var centre = new Vector2(b.x, b.z);
                var house = new House
                {
                    id = b.id, centre = centre, area = Mathf.Abs(GeoPolygon.Area(Outline(b))), guessed = Guessed(b),
                    street = streets != null && !streets.Empty ? streets.Distance(centre, Reach) : Reach
                };
                houses.Add(house);
                var key = new Vector2Int(Mathf.FloorToInt(centre.x / Cell), Mathf.FloorToInt(centre.y / Cell));
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<House>();
                list.Add(house);
            }
            foreach (var h in houses)
            {
                if (!h.guessed || h.area > 160) continue;
                int cx = Mathf.FloorToInt(h.centre.x / Cell), cz = Mathf.FloorToInt(h.centre.y / Cell);
                bool yard = false;
                for (int x = cx - 1; x <= cx + 1 && !yard; x++)
                    for (int z = cz - 1; z <= cz + 1 && !yard; z++)
                    {
                        if (!grid.TryGetValue(new Vector2Int(x, z), out var list)) continue;
                        foreach (var other in list)
                        {
                            if (other == h) continue;
                            float d = Vector2.Distance(other.centre, h.centre);
                            if (d < 30 && other.area >= Mathf.Max(1.2f * h.area, 45) && other.street <= h.street - 3) { yard = true; break; }
                            if (d < 30 && h.street >= Reach && other.area >= 1.5f * h.area) { yard = true; break; }
                            if (d < 15 && h.area < 45 && other.area >= 1.6f * h.area) { yard = true; break; }
                        }
                    }
                if (yard) result.Add(h.id);
            }
            return result;
        }

        sealed class House { public string id; public Vector2 centre; public float area, street; public bool guessed; }

        // a category found from the outline's size, not mapped (older exports say nothing: a bare building=yes is guessed)
        static bool Guessed(BuildingFeature b) =>
            b.categorySource != null ? b.categorySource == "guessed" : string.IsNullOrEmpty(b.buildingTag) || b.buildingTag == "yes";

        // A copy of a building drawn as a yard's outbuilding: the outbuilding models (sheds, summer kitchens, barns) and
        // the house style, never a house model with a porch to the street.
        public static BuildingFeature AsOutbuilding(BuildingFeature b)
        {
            var copy = b.Clone();
            copy.category = "shed"; copy.categorySource = "yard";
            return copy;
        }
    }
}
