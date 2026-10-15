# Geo World Generator

Unity worlds of any area of Ukraine from the exports of the Building Extractor app: terrain with satellite
imagery, roads with soft edges, rivers and lakes, and buildings drawn with ruin and home models according to the
building condition the app gives (damaged or not is decided in the app only). Large areas become tiled worlds
that stream 1 km tiles around the camera.

Unity 6000.3 with the Universal Render Pipeline 17.

## Install in a project

Package Manager > **+** > **Add package from git URL...** and enter the version you want:

```
https://github.com/tredokiller/geoworld-unity.git#v1.0.0
```

or add it to `Packages/manifest.json`:

```json
"com.tredokiller.geoworld": "https://github.com/tredokiller/geoworld-unity.git#v1.0.0"
```

The repository is private: git on the computer must be signed in to GitHub (the same login that can clone it).

## Update

Change the version after `#` in `Packages/manifest.json` (`#v1.0.0` to `#v1.1.0`) and go back to Unity: it
downloads that version. Each version is listed in [CHANGELOG.md](CHANGELOG.md). (Without a version after `#` Unity
keeps the commit it first downloaded, recorded in `Packages/packages-lock.json`, so a version is the dependable way.)

## First steps in a project

1. **Tools > Geo World > Set up rendering for large worlds** (GPU Resident Drawer, occlusion culling).
2. **Tools > Geo World > Generator window** › **New area…**: find a place on the map of Ukraine (search, or pan and
   zoom), press Draw area and drag the area (drag its corners to change it; at most 100 km²), name it, and press
   Extract. The Building Extractor app, which comes with
   the package (with its own Python on Windows: nothing to install), fetches the buildings, roads, terrain and the
   radar damage estimate in the background; a small town takes a few minutes. The export is then selected in the
   window: choose a tiled world or one world and a few switches, and press Generate. Every other setting is under
   Advanced settings. **Open full app** starts the app's web page (condition review, reports, news).

Exports go to `%LOCALAPPDATA%\GeoWorld\exports`; exports made elsewhere with the app can be picked with Browse….

**Updates.** Once per editor session the newest version is looked up; when there is a newer one the console and the
Scene view say so and the Geo World window shows an **Update** button. **Tools > Geo World > Update Geo World...**
does the same on demand.

The 25 ruin models and 38 home models come with the package (`Ruins`, `Homes`): nothing to import. Their textures are
kept small (colour as JPEG, normal maps as 1024 px PNG; about 310 MB for the whole package with the app). Everything the tools
make goes into the project's `Assets/GeoWorld` (generated worlds, tiles, reduced copies of the models, roofs tinted
to the export's colours); the package itself is read-only.

## Developing the package

It lives in `terrain-generator/Packages/com.tredokiller.geoworld` of the Building Extractor repository, where the
Unity project uses it directly. That project defines `GEOWORLD_DEVELOPMENT`, which adds **Tools > Geo World >
Developer**: **Import ruin models** / **Import home models** rebuild the package's libraries from `ruins/models` and
`intact/models` (they ask once for the folders and remember them), **Reduce library models again**, and the benchmark.
`py -3 tools/publish_unity_package.py <version> "<what changed>"` publishes a version for the other projects.

## More

The manual is in [Documentation~/Manual.txt](Documentation~/Manual.txt): settings, tiled worlds and streaming,
satellite imagery, building condition, ruin and home models, editing roads and buildings.
