using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace GeoWorld.Editor
{
    // New area: a map of Ukraine (satellite imagery with place names) to find a place (search, or pan and zoom) and draw
    // the area to extract with the Building Extractor app (GeoExtractor). The export lands in the exports folder the
    // Geo World window lists, ready to generate.
    // Drag: move the map. Shift + drag (or Draw area): the area. Drag a corner of the area: resize it. Wheel: zoom.
    // An area is at most MaxKm2 square kilometres, of any shape.
    public class GeoAreaWindow : EditorWindow
    {
        const string Imagery = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
        const string Labels = "https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}";
        const string Attribution = "Imagery © Esri, Maxar, Earthstar Geographics · Places © Esri · Search © OpenStreetMap";
        const int TileSize = 256, MinZoom = 5, MaxZoom = 18;
        const double LargeKm2 = 60, RadarKm2 = 250;
        public const double MaxKm2 = 100;
        const float Handle = 12, HandleReach = 9;
        static readonly Color AreaLine = new Color(0.45f, 0.68f, 1f), AtLimit = new Color(1f, 0.66f, 0.25f);

        [SerializeField] double centerLat = 48.6, centerLon = 31.3;
        [SerializeField] int zoom = 6;
        [SerializeField] bool hasArea;
        [SerializeField] double south, west, north, east;
        [SerializeField] string areaName = "";
        [SerializeField] bool radar = true;
        [SerializeField] string search = "";

        bool drawMode, dragging, drawing;
        Vector2 dragStart;
        double dragLat, dragLon;
        // drawing or resizing: the corner that stays (the start of the drag, or the corner across from the one dragged)
        double anchorLat, anchorLon;
        int resizing = -1;
        List<(string name, double s, double w, double n, double e)> results;
        UnityWebRequest searching;

        readonly Dictionary<string, Texture2D> tiles = new Dictionary<string, Texture2D>();
        readonly Dictionary<string, UnityWebRequest> loading = new Dictionary<string, UnityWebRequest>();
        readonly HashSet<string> failed = new HashSet<string>();
        readonly Queue<string> order = new Queue<string>();
        static string CacheFolder => Path.Combine(Application.dataPath, "..", "Library", "GeoWorld", "MapTiles");

        public static void Open()
        {
            var window = GetWindow<GeoAreaWindow>();
            window.titleContent = new GUIContent("New area", EditorGUIUtility.IconContent(EditorGUIUtility.isProSkin ? "d_Terrain Icon" : "Terrain Icon").image);
            window.minSize = new Vector2(520, 520);
        }

        void OnEnable() => GeoExtractor.Changed += Repaint;

        void OnDisable()
        {
            GeoExtractor.Changed -= Repaint;
            foreach (var request in loading.Values) request.Dispose();
            loading.Clear();
            foreach (var texture in tiles.Values) if (texture) DestroyImmediate(texture);
            tiles.Clear();
        }

        // ---------------------------------------------------------------- Web Mercator

        static double WorldX(double lon, int z) => (lon + 180.0) / 360.0 * TileSize * (1 << z);
        static double WorldY(double lat, int z)
        {
            double r = lat * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * TileSize * (1 << z);
        }
        static double Lon(double x, int z) => x / (TileSize * (double)(1 << z)) * 360.0 - 180.0;
        static double Lat(double y, int z)
        {
            double n = Math.PI - 2.0 * Math.PI * y / (TileSize * (double)(1 << z));
            return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
        }

        // size of the area in kilometres (east-west, north-south)
        public static (double width, double height) SizeKm(double s, double w, double n, double e)
        {
            double middle = (s + n) / 2 * Math.PI / 180;
            return ((e - w) * 111.32 * Math.Cos(middle), (n - s) * 111.32);
        }

        // The corner at (lat, lon), moved towards the anchor along the drag so that the area between them is at most
        // maxKm2 (as SizeKm measures it): the area keeps the shape of the drag and stops growing.
        public static (double lat, double lon) Limit(double anchorLat, double anchorLon, double lat, double lon, double maxKm2 = MaxKm2)
        {
            double dLat = lat - anchorLat, dLon = lon - anchorLon;
            double Km2()
            {
                var (width, height) = SizeKm(Math.Min(anchorLat, anchorLat + dLat), Math.Min(anchorLon, anchorLon + dLon),
                                             Math.Max(anchorLat, anchorLat + dLat), Math.Max(anchorLon, anchorLon + dLon));
                return Math.Abs(width * height);
            }
            if (Km2() <= maxKm2) return (lat, lon);
            // the width depends a little on the middle latitude, which moves with the corner: a few rounds settle it
            for (int round = 0; round < 5; round++)
            {
                double scale = Math.Sqrt(maxKm2 / Km2());
                dLat *= scale; dLon *= scale;
            }
            return (anchorLat + dLat, anchorLon + dLon);
        }

        // the area's corners on the map: north-west, north-east, south-east, south-west
        Vector2[] Corners(Rect map)
        {
            double left = WorldX(centerLon, zoom) - map.width / 2, top = WorldY(centerLat, zoom) - map.height / 2;
            float x0 = (float)(WorldX(west, zoom) - left) + map.x, x1 = (float)(WorldX(east, zoom) - left) + map.x;
            float y0 = (float)(WorldY(north, zoom) - top) + map.y, y1 = (float)(WorldY(south, zoom) - top) + map.y;
            return new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };
        }

        (double lat, double lon) Corner(int i) => (i < 2 ? north : south, i == 0 || i == 3 ? west : east);

        // the area from the anchor to the point under the mouse, at most MaxKm2
        void Stretch(Rect map, Vector2 mouse)
        {
            double left = WorldX(centerLon, zoom) - map.width / 2, top = WorldY(centerLat, zoom) - map.height / 2;
            var (lat, lon) = Limit(anchorLat, anchorLon, Lat(top + mouse.y - map.y, zoom), Lon(left + mouse.x - map.x, zoom));
            south = Math.Min(anchorLat, lat); north = Math.Max(anchorLat, lat);
            west = Math.Min(anchorLon, lon); east = Math.Max(anchorLon, lon);
        }

        bool TooLarge()
        {
            var (w, h) = SizeKm(south, west, north, east);
            return w * h > MaxKm2 + 0.05;
        }

        // ---------------------------------------------------------------- drawing

        void OnGUI()
        {
            wantsMouseMove = true; // the button and the corners change under the mouse
            if (Event.current.type == EventType.MouseMove) Repaint();
            DrawToolbar();
            var panel = 116f;
            var map = new Rect(0, 24, position.width, Mathf.Max(100, position.height - 24 - panel));
            DrawMap(map);
            HandleMap(map);
            DrawPanel(new Rect(0, map.yMax, position.width, panel));
        }

        void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUI.SetNextControlName("search");
            search = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(160));
            bool enter = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "search";
            if (GUILayout.Button("Find", EditorStyles.toolbarButton, GUILayout.Width(44)) || enter) Search();
            GUILayout.FlexibleSpace();
            var drawButton = GUILayoutUtility.GetRect(92, 18, GUILayout.Width(92), GUILayout.Height(18));
            drawButton.y += 1;
            drawMode = AccentToggle(drawButton, drawMode, drawMode ? "Drawing…" : "Draw area",
                drawMode ? "Drag on the map to draw the area; press again to stop" : "Drag on the map to draw the area (or hold Shift while dragging)");
            GUILayout.Space(4);
            using (new EditorGUI.DisabledScope(!hasArea))
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(48))) hasArea = false;
            if (GUILayout.Button("Ukraine", EditorStyles.toolbarButton, GUILayout.Width(58))) { centerLat = 48.6; centerLon = 31.3; zoom = 6; }
            GUILayout.EndHorizontal();
        }

        // The Geo World window's blue button (gw-accent: #3d7eff, lighter under the mouse) as a toggle: darker while on.
        static readonly Color Accent = new Color32(0x3d, 0x7e, 0xff, 0xff), AccentHover = new Color32(0x55, 0x90, 0xff, 0xff),
                              AccentOn = new Color32(0x2c, 0x62, 0xd4, 0xff);
        static GUIStyle accentText;

        static bool AccentToggle(Rect rect, bool on, string text, string tooltip)
        {
            accentText ??= new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 11, normal = { textColor = Color.white } };
            if (Event.current.type == EventType.Repaint)
            {
                var color = on ? AccentOn : rect.Contains(Event.current.mousePosition) ? AccentHover : Accent;
                GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, color, 0, 6);
                accentText.Draw(rect, text, false, false, false, false);
            }
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (GUI.Button(rect, new GUIContent("", tooltip), GUIStyle.none)) on = !on;
            return on;
        }

        void DrawMap(Rect map)
        {
            EditorGUI.DrawRect(map, new Color(0.13f, 0.15f, 0.18f));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.BeginClip(map);
                double cx = WorldX(centerLon, zoom), cy = WorldY(centerLat, zoom);
                double left = cx - map.width / 2, top = cy - map.height / 2;
                int count = 1 << zoom;
                int x0 = (int)Math.Floor(left / TileSize), x1 = (int)Math.Floor((left + map.width) / TileSize);
                int y0 = Math.Max(0, (int)Math.Floor(top / TileSize)), y1 = Math.Min(count - 1, (int)Math.Floor((top + map.height) / TileSize));
                foreach (var layer in new[] { Imagery, Labels })
                    for (int ty = y0; ty <= y1; ty++)
                        for (int tx = x0; tx <= x1; tx++)
                        {
                            var rect = new Rect((float)(tx * TileSize - left), (float)(ty * TileSize - top), TileSize, TileSize);
                            var texture = Tile(layer, zoom, ((tx % count) + count) % count, ty);
                            if (texture) GUI.DrawTexture(rect, texture);
                        }
                if (hasArea) DrawArea(map);
                GUI.EndClip();
            }
            // the corners show that they can be dragged
            if (hasArea && !drawMode)
            {
                var corners = Corners(map);
                for (int i = 0; i < 4; i++)
                {
                    var handle = new Rect(corners[i].x - HandleReach, corners[i].y - HandleReach, HandleReach * 2, HandleReach * 2);
                    if (map.Overlaps(handle)) EditorGUIUtility.AddCursorRect(handle, i % 2 == 0 ? MouseCursor.ResizeUpLeft : MouseCursor.ResizeUpRight);
                }
            }
            var note = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.LowerRight, normal = { textColor = new Color(1, 1, 1, 0.75f) } };
            GUI.Label(new Rect(map.x, map.yMax - 18, map.width - 6, 16), Attribution, note);
            if (loading.Count > 0) Repaint();
            if (!hasArea && !drawing)
            {
                var hint = new GUIStyle(EditorStyles.helpBox) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
                GUI.Label(new Rect(map.x + 10, map.y + 10, 330, 22), "Find a place, then press Draw area (or Shift + drag)", hint);
            }
        }

        // The area (inside the map's clip): its outline, a handle on each corner and its size; orange at the size limit.
        void DrawArea(Rect map)
        {
            var corners = Corners(map);
            for (int i = 0; i < 4; i++) corners[i] -= map.position;
            var area = Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
            var (w, h) = SizeKm(south, west, north, east);
            bool limit = w * h > MaxKm2 - 0.5;
            var line = limit ? AtLimit : AreaLine;
            EditorGUI.DrawRect(area, new Color(line.r, line.g, line.b, 0.16f));
            Handles.BeginGUI();
            Handles.color = line;
            Handles.DrawAAPolyLine(3, corners[0], corners[1], corners[2], corners[3], corners[0]);
            Handles.EndGUI();
            foreach (var corner in corners)
            {
                var outer = new Rect(corner.x - Handle / 2, corner.y - Handle / 2, Handle, Handle);
                EditorGUI.DrawRect(outer, line);
                EditorGUI.DrawRect(new Rect(outer.x + 2, outer.y + 2, Handle - 4, Handle - 4), Color.white);
            }
            string size = w.ToString("0.0", CultureInfo.InvariantCulture) + " × " + h.ToString("0.0", CultureInfo.InvariantCulture) + " km  ·  " +
                          (w * h).ToString("0", CultureInfo.InvariantCulture) + " km²" +
                          (limit ? " (at most " + MaxKm2.ToString("0", CultureInfo.InvariantCulture) + ")" : "");
            var style = new GUIStyle(EditorStyles.miniBoldLabel) { normal = { textColor = Color.white }, padding = new RectOffset(5, 5, 1, 1) };
            var content = new GUIContent(size);
            var box = style.CalcSize(content);
            // above the area, or inside it at the top of the map
            var label = new Rect(area.x, area.y - box.y - Handle / 2 - 2, box.x, box.y);
            if (label.y < 2) label.y = area.y + Handle / 2 + 2;
            EditorGUI.DrawRect(label, new Color(0, 0, 0, 0.6f));
            GUI.Label(label, content, style);
        }

        // the corner of the area under the mouse (its handle), or -1
        int HitCorner(Rect map, Vector2 mouse)
        {
            var corners = Corners(map);
            int hit = -1; float nearest = HandleReach;
            for (int i = 0; i < 4; i++)
            {
                float d = Mathf.Max(Mathf.Abs(corners[i].x - mouse.x), Mathf.Abs(corners[i].y - mouse.y));
                if (d <= nearest) { hit = i; nearest = d; }
            }
            return hit;
        }

        void HandleMap(Rect map)
        {
            var e = Event.current;
            if (!map.Contains(e.mousePosition) && !dragging && !drawing && resizing < 0) return;
            double left = WorldX(centerLon, zoom) - map.width / 2, top = WorldY(centerLat, zoom) - map.height / 2;
            switch (e.type)
            {
                case EventType.ScrollWheel:
                {
                    // zoom around the mouse
                    double mx = left + e.mousePosition.x - map.x, my = top + e.mousePosition.y - map.y;
                    double lat = Lat(my, zoom), lon = Lon(mx, zoom);
                    int next = Mathf.Clamp(zoom + (e.delta.y < 0 ? 1 : -1), MinZoom, MaxZoom);
                    if (next != zoom)
                    {
                        double nx = WorldX(lon, next) - (e.mousePosition.x - map.x) + map.width / 2;
                        double ny = WorldY(lat, next) - (e.mousePosition.y - map.y) + map.height / 2;
                        zoom = next; centerLon = Lon(nx, zoom); centerLat = Lat(ny, zoom);
                    }
                    e.Use();
                    break;
                }
                case EventType.MouseDown when e.button == 0:
                {
                    int corner = hasArea && !drawMode ? HitCorner(map, e.mousePosition) : -1;
                    if (corner >= 0) { resizing = corner; (anchorLat, anchorLon) = Corner((corner + 2) % 4); }
                    else if (drawMode || e.shift)
                    {
                        drawing = true; dragStart = e.mousePosition;
                        anchorLat = Lat(top + e.mousePosition.y - map.y, zoom); anchorLon = Lon(left + e.mousePosition.x - map.x, zoom);
                    }
                    else { dragging = true; dragStart = e.mousePosition; dragLat = centerLat; dragLon = centerLon; }
                    e.Use();
                    break;
                }
                case EventType.MouseDrag when resizing >= 0:
                case EventType.MouseUp when resizing >= 0:
                    // the corner across from the dragged one stays
                    Stretch(map, e.mousePosition);
                    if (e.type == EventType.MouseUp) resizing = -1;
                    e.Use();
                    break;
                case EventType.MouseDrag when dragging:
                {
                    var d = e.mousePosition - dragStart;
                    centerLon = Lon(WorldX(dragLon, zoom) - d.x, zoom);
                    centerLat = Math.Max(-84, Math.Min(84, Lat(WorldY(dragLat, zoom) - d.y, zoom)));
                    e.Use();
                    break;
                }
                case EventType.MouseDrag when drawing:
                case EventType.MouseUp when drawing:
                    if (Vector2.Distance(dragStart, e.mousePosition) > 4) { Stretch(map, e.mousePosition); hasArea = true; }
                    if (e.type == EventType.MouseUp) { drawing = false; drawMode = false; }
                    e.Use();
                    break;
                case EventType.MouseUp when dragging:
                    dragging = false;
                    e.Use();
                    break;
            }
            Repaint();
        }

        void DrawPanel(Rect rect)
        {
            GUILayout.BeginArea(new Rect(rect.x + 10, rect.y + 6, rect.width - 20, rect.height - 10));
            var job = GeoExtractor.Current;
            if (results != null && results.Count > 0 && (job == null || !job.Running))
            {
                GUILayout.Label("Places found:", EditorStyles.miniBoldLabel);
                GUILayout.BeginHorizontal();
                foreach (var r in results.GetRange(0, Math.Min(4, results.Count)))
                    if (GUILayout.Button(r.name, EditorStyles.miniButton, GUILayout.MaxWidth(180))) FlyTo(r);
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) results = null;
                GUILayout.EndHorizontal();
            }
            if (job != null && job.Running)
            {
                GUILayout.Label("Extracting " + job.name, EditorStyles.boldLabel);
                var bar = GUILayoutUtility.GetRect(10, 18, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(bar, job.progress, job.stage);
                if (GUILayout.Button("Cancel", GUILayout.Width(80))) GeoExtractor.Cancel();
            }
            else
            {
                if (job != null && job.Finished)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Extracted {job.name}: {job.buildings:N0} buildings.", EditorStyles.boldLabel);
                    if (GUILayout.Button("Open in Geo World window", GUILayout.Width(190))) GeoWorldWindow.OpenExport(job.package);
                    GUILayout.EndHorizontal();
                }
                else if (job != null && job.error != null) EditorGUILayout.HelpBox("The extraction failed: " + job.error, MessageType.Warning);
                if (!hasArea) GUILayout.Label("No area yet.", EditorStyles.miniLabel);
                else
                {
                    var (w, h) = SizeKm(south, west, north, east);
                    double km2 = w * h;
                    bool tooLarge = TooLarge();
                    var sizeStyle = new GUIStyle(EditorStyles.miniLabel);
                    if (tooLarge) sizeStyle.normal.textColor = AtLimit;
                    GUILayout.Label($"Area {w.ToString("0.0", CultureInfo.InvariantCulture)} × {h.ToString("0.0", CultureInfo.InvariantCulture)} km  ·  {km2.ToString("0.0", CultureInfo.InvariantCulture)} km²" +
                                    (tooLarge ? $"  ·  too large: at most {MaxKm2:0} km², drag a corner in"
                                        : (km2 > LargeKm2 ? "  ·  large: extracting takes a while" : "") + "  ·  drag a corner to change it"), sizeStyle);
                    GUILayout.BeginHorizontal();
                    areaName = EditorGUILayout.TextField("Name", areaName);
                    using (new EditorGUI.DisabledScope(km2 > RadarKm2))
                        radar = GUILayout.Toggle(radar && km2 <= RadarKm2, new GUIContent(" Damage from radar", km2 > RadarKm2
                            ? "Radar estimates take at most " + RadarKm2 + " km²: draw a smaller area" : "Estimates which buildings are damaged (Sentinel-1); a few minutes the first time"), GUILayout.Width(150));
                    using (new EditorGUI.DisabledScope(!GeoExtractor.Available || string.IsNullOrWhiteSpace(areaName) || tooLarge))
                        if (GUILayout.Button("Extract", GUILayout.Width(90), GUILayout.Height(20))) Extract();
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.Label(GeoExtractor.Available ? "Exports go to " + GeoExtractor.ExportsFolder : "The Building Extractor app is missing from this package.", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!GeoExtractor.Available))
                if (GUILayout.Button(new GUIContent("Open full app", "The Building Extractor web app: condition review, reports, news"), EditorStyles.miniButton, GUILayout.Width(100)))
                    Run(GeoExtractor.OpenFullApp);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void Extract() => Run(() => GeoExtractor.Extract(south, west, north, east, areaName.Trim(), radar));

        void Run(Action action)
        {
            try { action(); }
            catch (Exception e) { EditorUtility.DisplayDialog("New area", e.Message, "OK"); }
        }

        // ---------------------------------------------------------------- tiles

        // The texture of a map tile from memory or the disk cache; downloads it (in the background) when there is none.
        Texture2D Tile(string layer, int z, int x, int y)
        {
            string key = (layer == Imagery ? "i" : "l") + "/" + z + "/" + x + "/" + y;
            if (tiles.TryGetValue(key, out var texture)) return texture;
            if (loading.ContainsKey(key) || failed.Contains(key)) return null;
            string file = Path.Combine(CacheFolder, key.Replace('/', Path.DirectorySeparatorChar) + (layer == Imagery ? ".jpg" : ".png"));
            if (File.Exists(file))
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                if (texture.LoadImage(File.ReadAllBytes(file))) { Keep(key, texture); return texture; }
                DestroyImmediate(texture);
            }
            if (loading.Count >= 8) return null;
            var request = UnityWebRequest.Get(layer.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString()));
            request.SetRequestHeader("User-Agent", "GeoWorld-Unity/" + GeoUpdate.Installed);
            loading[key] = request;
            request.SendWebRequest().completed += _ =>
            {
                loading.Remove(key);
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var bytes = request.downloadHandler.data;
                    var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                    if (loaded.LoadImage(bytes))
                    {
                        Keep(key, loaded);
                        try { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, bytes); } catch (IOException) { }
                    }
                    else { DestroyImmediate(loaded); failed.Add(key); }
                }
                else failed.Add(key);
                request.Dispose();
                Repaint();
            };
            return null;
        }

        // keeps at most 400 tiles in memory, dropping the oldest
        void Keep(string key, Texture2D texture)
        {
            tiles[key] = texture;
            order.Enqueue(key);
            while (order.Count > 400)
            {
                string old = order.Dequeue();
                if (tiles.TryGetValue(old, out var t)) { DestroyImmediate(t); tiles.Remove(old); }
            }
        }

        // ---------------------------------------------------------------- places

        // Places in Ukraine matching the search text (OpenStreetMap Nominatim).
        void Search()
        {
            if (string.IsNullOrWhiteSpace(search) || searching != null) return;
            string url = "https://nominatim.openstreetmap.org/search?format=json&limit=6&countrycodes=ua&q=" + UnityWebRequest.EscapeURL(search.Trim());
            searching = UnityWebRequest.Get(url);
            searching.SetRequestHeader("User-Agent", "GeoWorld-Unity/" + GeoUpdate.Installed + " (Unity package com.tredokiller.geoworld)");
            searching.SendWebRequest().completed += _ =>
            {
                results = new List<(string, double, double, double, double)>();
                if (searching.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        foreach (var item in JArray.Parse(searching.downloadHandler.text))
                        {
                            var box = (JArray)item["boundingbox"];
                            string name = ((string)item["display_name"] ?? "").Split(',')[0];
                            results.Add((name, Parse(box[0]), Parse(box[2]), Parse(box[1]), Parse(box[3])));
                        }
                    }
                    catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is InvalidCastException || e is NullReferenceException) { }
                }
                if (results.Count == 0) results = null;
                else FlyTo(results[0]);
                searching.Dispose();
                searching = null;
                Repaint();
            };
        }

        static double Parse(JToken value) => double.Parse((string)value, CultureInfo.InvariantCulture);

        // centres the map on a place and zooms so it fills about half of the view; names the area after it
        void FlyTo((string name, double s, double w, double n, double e) place)
        {
            centerLat = (place.s + place.n) / 2;
            centerLon = (place.w + place.e) / 2;
            float width = Mathf.Max(200, position.width), height = Mathf.Max(200, position.height - 140);
            zoom = MaxZoom;
            while (zoom > MinZoom && (WorldX(place.e, zoom) - WorldX(place.w, zoom) > width * 0.6 || WorldY(place.s, zoom) - WorldY(place.n, zoom) > height * 0.6)) zoom--;
            if (string.IsNullOrWhiteSpace(areaName)) areaName = place.name;
            Repaint();
        }
    }
}
