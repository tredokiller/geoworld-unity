using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace GeoWorld.Editor
{
    // The Geo World window: pick an export of the Building Extractor app (a picture of the area, its size, roads,
    // buildings and damage), choose tiles or one world and four switches, and generate; every other setting is under
    // Advanced settings. The header shows the package's version and, when a newer one exists, an Update button.
    public class GeoWorldWindow : EditorWindow
    {
        [SerializeField] string exportFolder = "";
        [SerializeField] GenerationSettings settings = new GenerationSettings();
        [SerializeField] bool tiled = true;
        [SerializeField] float tileSize = GeoDefaults.TileSize;
        [SerializeField] string message = "";

        // the folder whose <area>_env exports are listed, chosen once per computer
        const string ExportsKey = "GeoWorld.ExportsFolder";
        readonly List<string> exports = new List<string>();
        DropdownField exportField;
        Label hint, versionChip, bannerText, status, extracting;
        string announced;
        VisualElement stats, banner;
        Image preview;
        Texture2D previewTexture;
        Button updateButton, tiledButton, singleButton, generateButton, rebuildButton;

        [MenuItem("Tools/Geo World/Generator window", false, 1)]
        public static void Open()
        {
            var window = GetWindow<GeoWorldWindow>();
            window.titleContent = new GUIContent("Geo World", EditorGUIUtility.IconContent(EditorGUIUtility.isProSkin ? "d_Terrain Icon" : "Terrain Icon").image);
            window.minSize = new Vector2(380, 520);
        }

        // Opens the window with an export (an <area>_env folder) selected.
        public static void OpenExport(string folder)
        {
            Open();
            var window = GetWindow<GeoWorldWindow>();
            window.exportFolder = folder ?? "";
            if (window.exportField != null) window.ListExports();
        }

        void OnEnable()
        {
            if (settings != null) GeoDefaults.FillLibraries(settings, false);
            GeoUpdate.Changed += ShowUpdate;
            GeoExtractor.Changed += ShowExtraction;
            Selection.selectionChanged += ShowSelection;
        }

        void OnDisable()
        {
            GeoUpdate.Changed -= ShowUpdate;
            GeoExtractor.Changed -= ShowExtraction;
            Selection.selectionChanged -= ShowSelection;
            if (previewTexture) DestroyImmediate(previewTexture);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(GeoPaths.Package + "/Editor/GeoWorldWindow.uss");
            if (sheet) root.styleSheets.Add(sheet);
            root.AddToClassList(EditorGUIUtility.isProSkin ? "gw-dark" : "gw-light");
            var page = new ScrollView();
            page.AddToClassList("gw-page");
            root.Add(page);

            var header = Row("gw-header");
            var titles = new VisualElement();
            titles.Add(Text("Geo World", "gw-title"));
            titles.Add(Text("Worlds of any area of Ukraine from the Building Extractor app", "gw-subtitle"));
            header.Add(titles);
            versionChip = Text("", "gw-chip");
            header.Add(versionChip);
            page.Add(header);

            banner = Row("gw-banner");
            bannerText = Text("", "gw-banner-text");
            banner.Add(bannerText);
            updateButton = new Button(GeoUpdate.UpdateNow) { text = "Update" };
            updateButton.AddToClassList("gw-accent");
            banner.Add(updateButton);
            page.Add(banner);

            var exportCard = Card(page, "1", "Export");
            var pick = Row("gw-pick");
            exportField = new DropdownField();
            exportField.AddToClassList("gw-grow");
            exportField.RegisterValueChangedCallback(e => SelectExport(exportField.index));
            pick.Add(exportField);
            pick.Add(new Button(Browse) { text = "Browse…" });
            exportCard.Add(pick);
            var extract = Row("gw-extract");
            var newArea = new Button(GeoAreaWindow.Open) { text = "New area…", tooltip = "Draw an area on the map and extract it with the Building Extractor app" };
            newArea.AddToClassList("gw-accent");
            extract.Add(newArea);
            var fullApp = new Button(() => Run(GeoExtractor.OpenFullApp)) { text = "Open full app", tooltip = "The Building Extractor web app: condition review, reports, news" };
            fullApp.AddToClassList("gw-subtle");
            extract.Add(fullApp);
            extracting = Text("", "gw-hint");
            extract.Add(extracting);
            newArea.SetEnabled(GeoExtractor.Available);
            fullApp.SetEnabled(GeoExtractor.Available);
            exportCard.Add(extract);
            preview = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            preview.AddToClassList("gw-preview");
            exportCard.Add(preview);
            stats = Row("gw-stats");
            exportCard.Add(stats);
            hint = Text("", "gw-hint");
            exportCard.Add(hint);

            var worldCard = Card(page, "2", "World");
            var segment = Row("gw-segment");
            tiledButton = new Button(() => SetTiled(true)) { text = "Tiled  ·  towns, streams" };
            singleButton = new Button(() => SetTiled(false)) { text = "One world  ·  small areas" };
            segment.Add(tiledButton);
            segment.Add(singleButton);
            worldCard.Add(segment);
            worldCard.Add(Option("Satellite imagery", "Imagery of the area draped over the terrain",
                () => settings.satelliteImagery, v => settings.satelliteImagery = v));
            worldCard.Add(Option("Damage from the app", "Ruin models wherever the export says a building is damaged",
                () => settings.applyCondition, v => settings.applyCondition = v));
            worldCard.Add(Option("Roads as meshes", "Road surfaces with soft edges over the imagery",
                () => settings.roadMeshes, v => settings.roadMeshes = v));
            worldCard.Add(Option("Trees and vegetation", "Mapped trees, tree rows and green areas",
                () => settings.trees && settings.vegetation, v => { settings.trees = v; settings.vegetation = v; }));
            var advanced = new Foldout { text = "Advanced settings", value = false };
            advanced.AddToClassList("gw-advanced");
            advanced.Add(new IMGUIContainer(DrawAdvanced));
            worldCard.Add(advanced);

            generateButton = new Button(Generate);
            generateButton.AddToClassList("gw-generate");
            page.Add(generateButton);

            var more = Row("gw-more");
            rebuildButton = new Button(RebuildSelected) { text = "Rebuild the selected world with these settings" };
            rebuildButton.AddToClassList("gw-grow");
            more.Add(rebuildButton);
            var sample = new Button(() => Run(GeoSample.CreateScene)) { text = "Sample scene", tooltip = "A small made-up riverside village" };
            sample.AddToClassList("gw-subtle");
            more.Add(sample);
            page.Add(more);
            status = Text("", "gw-status");
            status.selection.isSelectable = true;
            page.Add(status);

            ListExports();
            ShowExtraction();
            SetTiled(tiled);
            ShowUpdate();
            ShowSelection();
            ShowStatus(message);
            if (GeoUpdate.State == GeoUpdate.Status.Unknown) GeoUpdate.Check();
        }

        // ---------------------------------------------------------------- export

        string ExportsFolder
        {
            get
            {
                string chosen = EditorPrefs.GetString(ExportsKey, "");
                if (Directory.Exists(chosen)) return chosen;
                // the exports of the app that comes with the package (New area… puts them there)
                if (GeoExtractor.Available) { Directory.CreateDirectory(GeoExtractor.ExportsFolder); return GeoExtractor.ExportsFolder; }
                return "";
            }
        }

        void ListExports()
        {
            exports.Clear();
            string folder = ExportsFolder;
            if (folder != "")
                exports.AddRange(Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, "environment.json")))
                    .OrderByDescending(Directory.GetLastWriteTimeUtc));
            if (File.Exists(Path.Combine(exportFolder ?? "", "environment.json")) && !exports.Contains(exportFolder)) exports.Insert(0, exportFolder);
            var labels = new List<string>();
            foreach (var path in exports)
            {
                string label = Describe(path), unique = label;
                for (int n = 2; labels.Contains(unique); n++) unique = label + " (" + n + ")";
                labels.Add(unique);
            }
            exportField.choices = labels;
            int index = Math.Max(0, exports.IndexOf(exportFolder));
            if (exports.Count == 0)
            {
                exportField.SetValueWithoutNotify("No exports found");
                SelectExport(-1);
                return;
            }
            exportField.SetValueWithoutNotify(labels[index]);
            SelectExport(index);
        }

        // "lyman_48.9850N_37.8050E_20261012-104423_env" -> "Lyman  ·  12 Oct 2026, 10:44"
        static string Describe(string folder)
        {
            string name = Path.GetFileName(folder);
            var m = Regex.Match(name, @"^(.*?)_(\d+\.\d+)N_(\d+\.\d+)E_(\d{8})-(\d{4})");
            if (!m.Success) return name;
            string area = m.Groups[1].Value == "buildings" ? m.Groups[2].Value + " N, " + m.Groups[3].Value + " E"
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Groups[1].Value.Replace('_', ' ').Replace('-', ' '));
            string when = DateTime.TryParseExact(m.Groups[4].Value + m.Groups[5].Value, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) : m.Groups[4].Value;
            return area + "  ·  " + when + (name.Contains("_condition_") ? "  ·  damage" : "");
        }

        void SelectExport(int index)
        {
            exportFolder = index >= 0 && index < exports.Count ? exports[index] : "";
            stats.Clear();
            if (previewTexture) DestroyImmediate(previewTexture);
            previewTexture = null;
            preview.image = null;
            preview.style.display = DisplayStyle.None;
            if (exportFolder == "")
            {
                hint.text = ExportsFolder == ""
                    ? "Browse for the app's exports folder, or for an unzipped Terrain & ground package (<area>_env)."
                    : "No Terrain & ground exports in " + ExportsFolder + ". Export one from the app, or browse for another folder.";
                hint.style.display = DisplayStyle.Flex;
                UpdateGenerate();
                return;
            }
            hint.style.display = DisplayStyle.None;
            try
            {
                var env = JObject.Parse(File.ReadAllText(Path.Combine(exportFolder, "environment.json")));
                if (env["bounds"] is JObject b)
                    Stat(Km((float)b["maxX"] - (float)b["minX"]) + " × " + Km((float)b["maxZ"] - (float)b["minZ"]) + " km");
                var roads = env.SelectToken("summary.roads.km");
                if (roads != null) Stat(((float)roads).ToString("N0", CultureInfo.InvariantCulture) + " km of roads");
                var condition = env.SelectToken("conditionLayer.summary");
                if (condition != null)
                {
                    Stat(((int)condition["buildings"]).ToString("N0", CultureInfo.InvariantCulture) + " buildings");
                    int damaged = (int?)condition["withCondition"] ?? 0;
                    Stat(damaged.ToString("N0", CultureInfo.InvariantCulture) + " damaged", damaged > 0 ? "gw-stat-damage" : null);
                }
                else Stat("no damage data: drawn whole", "gw-stat-muted");
            }
            catch (Exception e) when (e is IOException || e is Newtonsoft.Json.JsonException || e is InvalidCastException || e is FormatException)
            {
                Stat("environment.json could not be read", "gw-stat-damage");
            }
            foreach (var image in new[] { "materials_preview.png", "hillshade.png" })
            {
                string path = Path.Combine(exportFolder, image);
                if (!File.Exists(path)) continue;
                previewTexture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                if (previewTexture.LoadImage(File.ReadAllBytes(path)))
                {
                    preview.image = previewTexture;
                    preview.style.display = DisplayStyle.Flex;
                    break;
                }
                DestroyImmediate(previewTexture);
                previewTexture = null;
            }
            UpdateGenerate();
        }

        static string Km(float metres) => (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture);

        void Stat(string text, string extra = null)
        {
            var label = Text(text, "gw-stat");
            if (extra != null) label.AddToClassList(extra);
            stats.Add(label);
        }

        void Browse()
        {
            string start = exportFolder != "" ? Path.GetDirectoryName(exportFolder) : ExportsFolder;
            string chosen = EditorUtility.OpenFolderPanel("The app's exports folder, or one export (<area>_env)", start, "");
            if (string.IsNullOrEmpty(chosen)) return;
            if (File.Exists(Path.Combine(chosen, "environment.json")))
            {
                exportFolder = chosen;
                chosen = Path.GetDirectoryName(chosen);
            }
            EditorPrefs.SetString(ExportsKey, chosen);
            ListExports();
        }

        // ---------------------------------------------------------------- world

        void SetTiled(bool on)
        {
            tiled = on;
            tiledButton.EnableInClassList("gw-selected", on);
            singleButton.EnableInClassList("gw-selected", !on);
            UpdateGenerate();
        }

        void UpdateGenerate()
        {
            if (generateButton == null) return;
            generateButton.text = tiled ? "Generate tiled world" : "Generate world";
            generateButton.SetEnabled(exportFolder != "" && !EditorApplication.isPlaying);
        }

        void Generate()
        {
            string environment = Path.Combine(exportFolder, "environment.json"), buildings = Path.Combine(exportFolder, "buildings.json");
            if (!File.Exists(buildings)) buildings = "";
            Run(() =>
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                GeoTerrainTextures.EnsureImported();
                if (!GeoRoadPhotos.Available) GeoRoadTextures.EnsureGenerated();
                GeoDefaults.FillLibraries(settings, true);
                if (tiled) ShowStatus(GeoTiles.Generate(environment, buildings, settings, tileSize).report);
                else
                {
                    var world = GeoGenerator.Generate(environment, buildings, settings, null);
                    ShowStatus(world.report);
                    Frame(world);
                }
            });
        }

        // The selected world (one world, or a whole tiled world) generated again from its export with these settings.
        void RebuildSelected()
        {
            var go = Selection.activeGameObject;
            var tiledWorld = go ? go.GetComponentInParent<GeoTiledWorld>() : null;
            var world = go ? go.GetComponentInParent<GeoWorldRoot>() : null;
            Run(() =>
            {
                if (tiledWorld) ShowStatus(GeoTiles.Generate(tiledWorld.environmentPath, tiledWorld.buildingsPath, settings, tiledWorld.tileSize, tiledWorld.folder).report);
                else if (world && !world.tile)
                {
                    world = GeoGenerator.Generate(world.environmentPath, world.buildingsPath, settings, world);
                    ShowStatus(world.report);
                    Frame(world);
                }
            });
        }

        void ShowSelection()
        {
            if (rebuildButton == null) return;
            var go = Selection.activeGameObject;
            var world = go ? go.GetComponentInParent<GeoWorldRoot>() : null;
            bool any = go && (go.GetComponentInParent<GeoTiledWorld>() || (world && !world.tile));
            rebuildButton.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void DrawAdvanced()
        {
            EditorGUIUtility.labelWidth = 200;
            Section("Terrain and roads");
            if (tiled) tileSize = EditorGUILayout.Slider("Tile size (m)", tileSize, 250, 4000);
            settings.heightResolution = EditorGUILayout.IntPopup(tiled ? "Heightmap resolution (a tile)" : "Heightmap resolution", settings.heightResolution, new[] { "257", "513", "1025" }, new[] { 257, 513, 1025 });
            settings.textureResolution = EditorGUILayout.IntPopup("Ground texture resolution", settings.textureResolution, new[] { "256", "512", "1024" }, new[] { 256, 512, 1024 });
            settings.roadPaintResolution = EditorGUILayout.IntPopup("Road paint detail", settings.roadPaintResolution, new[] { "1024", "2048", "4096" }, new[] { 1024, 2048, 4096 });
            settings.pathStep = EditorGUILayout.Slider("Road segment length (m)", settings.pathStep, 0.5f, 8);
            settings.roadShoulder = EditorGUILayout.Slider("Road shoulder (m)", settings.roadShoulder, 0, 8);
            settings.junctionSnap = EditorGUILayout.Slider("Junction snap distance (m)", settings.junctionSnap, 0, 3);
            settings.blendMetres = EditorGUILayout.Slider("Ground blend (m)", settings.blendMetres, 0, 10);
            settings.riverDepth = EditorGUILayout.Slider("River depth (m)", settings.riverDepth, 0.5f, 5);
            settings.gradeRoads = EditorGUILayout.Toggle("Grade terrain under roads", settings.gradeRoads);
            settings.carveRivers = EditorGUILayout.Toggle("Carve river beds", settings.carveRivers);
            settings.laneMarkings = EditorGUILayout.Toggle("Road centre markings", settings.laneMarkings);
            settings.groundTextures = EditorGUILayout.Toggle("Photographic ground textures", settings.groundTextures);
            if (settings.satelliteImagery)
            {
                Section("Satellite imagery");
                settings.satelliteResolution = EditorGUILayout.IntPopup("Texture size", settings.satelliteResolution, new[] { "Auto (0.4 m a texel)", "1024", "2048", "4096", "8192" }, new[] { 0, 1024, 2048, 4096, 8192 });
                settings.satelliteMaxZoom = EditorGUILayout.IntSlider("Deepest tile zoom", settings.satelliteMaxZoom, 1, 22);
                settings.satelliteTiles = EditorGUILayout.TextField("Tile URL {z}/{x}/{y}", settings.satelliteTiles);
                EditorGUILayout.HelpBox(GeoSatellite.Attribution(settings.satelliteTiles) + ". Tiles are cached in Library/GeoWorld/SatelliteTiles; check the server's terms before shipping the imagery.", MessageType.None);
            }
            Section("Buildings");
            settings.buildings = EditorGUILayout.Toggle("Buildings", settings.buildings);
            using (new EditorGUI.DisabledScope(!settings.buildings))
            {
                settings.buildingMode = (GeoBuildingMode)EditorGUILayout.EnumPopup("Building mode", settings.buildingMode);
                settings.mergeAbove = EditorGUILayout.IntSlider("Merge above (buildings)", settings.mergeAbove, 100, 20000);
                settings.chunkSize = EditorGUILayout.Slider("Merged chunk size (m)", settings.chunkSize, 50, 500);
                settings.mergedColliders = EditorGUILayout.Toggle("Colliders on merged chunks", settings.mergedColliders);
                settings.estimatedRoofs = EditorGUILayout.Toggle("Typical roofs where not mapped", settings.estimatedRoofs);
                using (new EditorGUI.DisabledScope(!settings.applyCondition))
                {
                    settings.ruinModels = (GeoRuinUse)EditorGUILayout.EnumPopup("Ruin models for", settings.ruinModels);
                    settings.ruinApron = EditorGUILayout.Slider("Ruin yard and debris (m)", settings.ruinApron, 0, 12);
                }
                settings.homeModels = (GeoHomeUse)EditorGUILayout.EnumPopup("Home models for", settings.homeModels);
                settings.reducedModels = EditorGUILayout.Toggle("Reduced models in a merged city", settings.reducedModels);
                settings.instancedHomes = EditorGUILayout.Toggle("Homes as model instances", settings.instancedHomes);
                settings.ruinLibrary = (GeoRuinLibrary)EditorGUILayout.ObjectField("Ruin library", settings.ruinLibrary, typeof(GeoRuinLibrary), false);
                settings.homeLibrary = (GeoHomeLibrary)EditorGUILayout.ObjectField("Home library", settings.homeLibrary, typeof(GeoHomeLibrary), false);
            }
            Section("Props");
            settings.mappedProps = EditorGUILayout.Toggle("Mapped street props, fences", settings.mappedProps);
            settings.streetDecoration = EditorGUILayout.Toggle("Generated street decoration", settings.streetDecoration);
            settings.propCatalog = (GeoPropCatalog)EditorGUILayout.ObjectField("Prop prefab catalog", settings.propCatalog, typeof(GeoPropCatalog), false);
            settings.treeSpacing = EditorGUILayout.Slider("Tree spacing (m)", settings.treeSpacing, 3, 25);
            settings.vegetationDensity = EditorGUILayout.Slider("Vegetation density", settings.vegetationDensity, 0, 1);
            settings.decorationSeed = EditorGUILayout.IntField("Decoration seed", settings.decorationSeed);
            settings.maxProps = EditorGUILayout.IntSlider("Maximum props", settings.maxProps, 100, 200000);
            settings.mergeProps = EditorGUILayout.Toggle("Bake props in a merged city", settings.mergeProps);
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Reset to the standard settings", GUILayout.Width(220)))
            {
                settings = GeoDefaults.Settings();
                EditorApplication.delayCall += () => { rootVisualElement.Clear(); CreateGUI(); };
            }
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        // An extraction (New area…) in progress, and the new export selected once it is there.
        void ShowExtraction()
        {
            if (extracting == null) return;
            var job = GeoExtractor.Current;
            extracting.text = job == null ? "" : job.Running ? $"Extracting {job.name}: {job.stage} ({job.progress:P0})" :
                job.error != null ? "Extracting " + job.name + " failed: " + job.error : job.Finished ? $"{job.name}: {job.buildings:N0} buildings extracted" : "";
            if (job != null && job.Finished && job.package != null && job.package != announced)
            {
                announced = job.package;
                exportFolder = job.package;
                ListExports();
            }
        }

        // ---------------------------------------------------------------- update and status

        void ShowUpdate()
        {
            if (versionChip == null) return;
            var state = GeoUpdate.State;
            versionChip.text = "v" + GeoUpdate.Installed + (state == GeoUpdate.Status.Developing ? "  ·  development" :
                state == GeoUpdate.Status.UpToDate ? "  ·  up to date" : state == GeoUpdate.Status.Checking ? "  ·  checking…" : "");
            versionChip.tooltip = state == GeoUpdate.Status.Failed ? "The update check failed: " + GeoUpdate.Error : "";
            bool show = state == GeoUpdate.Status.Available || state == GeoUpdate.Status.Updating;
            banner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            bannerText.text = state == GeoUpdate.Status.Updating ? "Updating to Geo World " + GeoUpdate.Latest + "…" : "Geo World " + GeoUpdate.Latest + " is available";
            updateButton.SetEnabled(state == GeoUpdate.Status.Available);
        }

        void ShowStatus(string text)
        {
            message = text ?? "";
            if (status == null) return;
            status.text = message.Length > 1500 ? message.Substring(0, 1500) + "…" : message;
            status.style.display = message == "" ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void Run(Action action)
        {
            try { action(); }
            catch (OperationCanceledException e) { ShowStatus(e.Message); }
            catch (Exception e) { ShowStatus(e.Message); Debug.LogException(e); }
        }

        // ---------------------------------------------------------------- building blocks

        static VisualElement Row(string className)
        {
            var row = new VisualElement();
            row.AddToClassList(className);
            return row;
        }

        static Label Text(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }

        static VisualElement Card(VisualElement parent, string step, string title)
        {
            var card = Row("gw-card");
            var head = Row("gw-card-head");
            head.Add(Text(step, "gw-step"));
            head.Add(Text(title, "gw-card-title"));
            card.Add(head);
            parent.Add(card);
            return card;
        }

        // A switch with its name and a line on what it does; the text toggles it too.
        static VisualElement Option(string name, string description, Func<bool> get, Action<bool> set)
        {
            var row = Row("gw-option");
            var toggle = new Toggle { value = get() };
            toggle.AddToClassList("gw-option-toggle");
            toggle.RegisterValueChangedCallback(e => set(e.newValue));
            var text = new VisualElement();
            text.AddToClassList("gw-option-text");
            text.Add(Text(name, "gw-option-name"));
            text.Add(Text(description, "gw-option-description"));
            text.RegisterCallback<ClickEvent>(e => toggle.value = !toggle.value);
            row.Add(toggle);
            row.Add(text);
            return row;
        }

        public static void Frame(GeoWorldRoot world)
        {
            if (world.terrain == null || SceneView.lastActiveSceneView == null) return;
            Vector3 size = world.terrain.terrainData.size;
            SceneView.lastActiveSceneView.LookAt(world.terrain.transform.position + size * 0.5f, Quaternion.Euler(55, -25, 0), Mathf.Max(size.x, size.z) * 0.65f);
        }
    }

    [CustomEditor(typeof(GeoWorldRoot))]
    public class GeoWorldRootEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var root=(GeoWorldRoot)target;
            EditorGUILayout.HelpBox(root.report??"Generated world",MessageType.Info);
            // a world made before the ruin models were imported has no library: offer the project's
            if(root.settings!=null&&!root.settings.ruinLibrary&&root.settings.ruinModels!=GeoRuinUse.Off&&GeoRuinImporter.Library)
            {
                EditorGUILayout.HelpBox("The ruin models are imported, but this world has no Ruin Library: its damaged buildings are drawn with procedural damage.",MessageType.Warning);
                if(GUILayout.Button("Use the project's ruin library (then Rebuild buildings only)"))
                {Undo.RecordObject(root,"Use ruin library");root.settings.ruinLibrary=GeoRuinImporter.Library;EditorUtility.SetDirty(root);}
            }
            DrawDefaultInspector();
            if(GUILayout.Button("Rebuild roads, rivers and terrain",GUILayout.Height(32)))
                try {GeoGenerator.Generate(root.environmentPath,root.buildingsPath,root.settings,root);}catch(Exception e){Debug.LogException(e);}
            if(GUILayout.Button("Rebuild buildings only",GUILayout.Height(30)))
                try {GeoGenerator.RebuildBuildings(root);}catch(Exception e){Debug.LogException(e);}
            if(GUILayout.Button("Rebuild props and vegetation only",GUILayout.Height(30)))
                try {GeoPropBuilder.Rebuild(root);}catch(Exception e){Debug.LogException(e);}
            if(GUILayout.Button("Frame world"))GeoWorldWindow.Frame(root);
            EditorGUILayout.HelpBox("Select a Road or River child to edit its width or move its control points. Generated assets are saved under Assets/GeoWorld/Generated.",MessageType.None);
        }
    }

    [CustomEditor(typeof(GeoPath))]
    public class GeoPathEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var path=(GeoPath)target;
            EditorGUILayout.LabelField(path.river?"River path":"Road path",EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Source",path.sourceId);
            EditorGUILayout.LabelField("Source layer",path.layer.ToString());
            EditorGUILayout.HelpBox("Move the numbered nodes in Scene view or edit their X/Z values. Rebuild applies the path and width, regrades the terrain and repaints roads, lines and shoulders. Y is sampled automatically.",MessageType.Info);
            DrawDefaultInspector();
            if(GUILayout.Button("Insert node in longest segment"))
            {
                if(path.points.Count>=2)
                {
                    Undo.RecordObject(path,"Insert path node");int index=1;float length=0;
                    for(int i=1;i<path.points.Count;i++) {float d=(path.points[i]-path.points[i-1]).sqrMagnitude;if(d>length){length=d;index=i;}}
                    path.points.Insert(index,(path.points[index-1]+path.points[index])*0.5f);EditorUtility.SetDirty(path);
                }
            }
            if(GUILayout.Button("Apply path & rebuild world",GUILayout.Height(32)))
            {
                var world=path.GetComponentInParent<GeoWorldRoot>();
                if(world!=null)try{GeoGenerator.Generate(world.environmentPath,world.buildingsPath,world.settings,world);}catch(Exception e){Debug.LogException(e);}
            }
        }

        void OnSceneGUI()
        {
            var path=(GeoPath)target;
            Handles.color=path.river?Color.cyan:new Color(1,0.8f,0.15f);
            for(int i=0;i<path.points.Count;i++)
            {
                Vector3 point=path.points[i];
                Handles.Label(point+Vector3.up, i.ToString());
                EditorGUI.BeginChangeCheck();
                Vector3 moved=Handles.Slider2D(point,Vector3.up,Vector3.right,Vector3.forward,
                    HandleUtility.GetHandleSize(point)*0.09f,Handles.DotHandleCap,Vector2.zero);
                if(EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(path,"Move path node");path.points[i]=new Vector3(moved.x,point.y,moved.z);
                    EditorUtility.SetDirty(path);EditorSceneManager.MarkSceneDirty(path.gameObject.scene);
                }
                if(i>0) Handles.DrawAAPolyLine(3,path.points[i-1],path.points[i]);
            }
        }
    }
}
