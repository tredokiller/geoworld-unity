using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // What the generator makes of an export's buildings, without building them: the kinds GeoBuildingKinds finds for the
    // guessed ones, which home models each kind then gets (GeoHomes.Assign with the site the generator gives it), how far
    // they are stretched and which way their fronts look. A check on a real town (Bakhmut's battlefield, Chuhuiv) before
    // generating it; kinds false shows the export's own guesses.
    public static class GeoBuildingReport
    {
        public static string Run(string buildingsPath, string environmentPath, bool kinds = true)
        {
            var env = GeoData.ReadEnvironment(environmentPath);
            var data = GeoData.ReadBuildings(buildingsPath, env.origin);
            var settings = new GenerationSettings
            {
                inferBuildingKinds = kinds,
                homeLibrary = AssetDatabase.LoadAssetAtPath<GeoHomeLibrary>(GeoHomeLibrary.DefaultPath),
                ruinLibrary = AssetDatabase.LoadAssetAtPath<GeoRuinLibrary>(GeoRuinLibrary.DefaultPath),
            };
            var streets = new GeoStreets(env.roads);
            var site = GeoHomeSites.For(data.buildings, streets, settings, env.areas);
            var temp = new GameObject("Geo building report (temporary)") { hideFlags = HideFlags.HideAndDontSave };
            var drawn = new Dictionary<string, Dictionary<string, int>>();
            var changed = new Dictionary<string, int>();
            var stretch = new List<float>();
            int homes = 0, sections = 0, ruins = 0, procedural = 0, wrongKind = 0, facingStreet = 0, facingAway = 0;
            try
            {
                GeoRuinFit.ClearCache(); GeoHomePlan.ClearCache();
                foreach (var listed in data.buildings)
                {
                    if (!GeoWorldBuild.ValidBuilding(listed)) continue;
                    var source = GeoHomeSites.Drawn(GeoWorldBuild.ForSettings(listed, settings), site);
                    if (source.category != listed.category) Add(changed, listed.category + " -> " + source.category);
                    var building = temp.AddComponent<GeoBuilding>();
                    try
                    {
                        GeoBuildingBuilder.Import(building, source, null);
                        GeoRuins.Assign(building, settings, site.street);
                        GeoHomes.Assign(building, settings, site);
                        string what;
                        if (GeoHomes.Draws(building))
                        {
                            homes++;
                            if (building.homeParts != null && building.homeParts.Count > 1) sections++;
                            string kind = Kind(building.home);
                            what = "home " + kind;
                            if (Residential(kind) && NonResidential(source.category)) wrongKind++;
                            var frame = GeoRuinFit.FrameOf(GeoPolygon.Clean(building.footprint));
                            float parts = building.homeParts != null ? building.homeParts.Count : 1;
                            stretch.Add(Mathf.Sqrt(frame.length * frame.width / parts / Mathf.Max(1, building.home.length * building.home.width)));
                            var toward = site.street?.Invoke(new Vector2(source.x, source.z));
                            if (toward != null && building.homeParts == null)
                            {
                                var front = GeoHomes.Front(frame, building.homeQuarter, building.homeTurn);
                                var world = building.transform.TransformDirection(new Vector3(front.x, 0, front.y));
                                float dot = Vector2.Dot(new Vector2(world.x, world.z).normalized, toward.Value);
                                if (dot > 0.5f) facingStreet++; else if (dot < -0.5f) facingAway++;
                            }
                        }
                        else if (GeoRuins.Draws(building)) { ruins++; what = "ruin"; }
                        else { procedural++; what = "procedural"; }
                        string key = source.category + " (" + (source.categorySource ?? "-") + ")";
                        if (!drawn.TryGetValue(key, out var row)) drawn[key] = row = new Dictionary<string, int>();
                        Add(row, what);
                    }
                    catch (System.ArgumentException) { procedural++; }
                    finally { Object.DestroyImmediate(building); }
                }
            }
            finally { Object.DestroyImmediate(temp); }

            var report = new StringBuilder();
            report.AppendLine($"{Path.GetFileName(buildingsPath)}: kinds {(kinds ? "on" : "off")}; {homes} homes ({sections} in sections), {ruins} ruins, {procedural} procedural");
            report.AppendLine($"residential models on non-residential buildings: {wrongKind}");
            stretch.Sort();
            if (stretch.Count > 0)
                report.AppendLine($"home size against the model (sqrt of areas): p05 {stretch[stretch.Count / 20]:F2} p50 {stretch[stretch.Count / 2]:F2} p95 {stretch[stretch.Count * 19 / 20]:F2}, outside 0.8..1.25: {stretch.Count(s => s < 0.8f || s > 1.25f) * 100f / stretch.Count:F0} %");
            report.AppendLine($"single homes with a street in reach: front to it {facingStreet}, away from it {facingAway}");
            if (changed.Count > 0) report.AppendLine("kinds changed: " + string.Join(", ", changed.OrderByDescending(p => p.Value).Select(p => p.Key + " " + p.Value)));
            foreach (var pair in drawn.OrderByDescending(p => p.Value.Values.Sum()))
                report.AppendLine($"  {pair.Key}: " + string.Join(", ", pair.Value.OrderByDescending(p => p.Value).Select(p => p.Key + " " + p.Value)));
            return report.ToString();
        }

        static void Add(Dictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;

        // a model's kind: its first category
        static string Kind(GeoHomeModelAsset model) => model != null && model.categories != null && model.categories.Length > 0 ? model.categories[0] : "-";

        static bool Residential(string kind) => kind == "house" || kind == "apartments";

        static bool NonResidential(string category) => GeoHomeLibrary.NonResidential(category);
    }
}
