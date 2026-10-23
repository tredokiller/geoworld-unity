# Geo World Generator

Unity worlds of any area of Ukraine from the exports of the Building Extractor app: terrain with satellite
imagery, roads with soft edges, rivers and lakes, and buildings drawn with ruin and home models according to the
building condition the app gives (damaged or not is decided in the app only). Large areas become tiled worlds
that stream 1 km tiles around the camera. The exports of the parts of one large area (a city cut into parts) make one
world on one shared grid.

Unity 6000.3 with the Universal Render Pipeline 17.

## Tile scenes and external trees

Geo World requires Addressables 2.9.1 and UniTask 2.5.10. Install UniTask before importing this package:

```
https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#2.5.10
```

New tiled worlds use **Addressable Scenes**. Generation registers each tile scene in its world's Addressables group,
with one scene per bundle, and puts the assets two or more tiles use (models, textures, shaders) into one shared group, so the
build holds one copy of them instead of one per tile bundle. The master scene goes in Build Settings; build Addressables content using
**Window > Asset Management > Addressables > Groups > Build > New Build > Default Build Script** before a player build, or
use **Tools > Geo World > Build player of the open world...**, which does all of it and reports what the build weighs.
For an existing world, enable **Addressable Scenes** on its Geo Tiled World and choose **Rebuild changed tiles**.
With Addressable Scenes disabled, add the tile scenes to Build Settings using the world's Inspector.

Tile generation automatically exports terrain tree instances and Geo World's instanced tree batches to
`Assets/Resources/TerrainChunker/TreeData`. Keys include the world's output folder and tile coordinates, so worlds with
identical terrain names do not collide. Rebuilding updates the same asset and preserves its GUID, including when a
new generation has no trees. Tree prototypes stay in TerrainData; the saved tile contains no tree instances.

`GeoTerrainTreeLoader` loads the tile's tree asset asynchronously, converts terrain trees in batches, and restores them
on a runtime copy of TerrainData. Unloading releases that copy and its tree batches. Nature Renderer starts after the
trees are ready and retains its frame spacing. The external assets use Geo World's own types; TerrainChunker is not
required. Regenerate old tiles to produce these assets. Trees are restored in Play mode; stripped tile scenes do not
show their external trees in Edit mode.

## Install in a project

Package Manager > **+** > **Add package from git URL...** and enter the version you want:

```
https://github.com/tredokiller/geoworld-unity.git#v1.9.1
```

or add it to `Packages/manifest.json`:

```json
"com.tredokiller.geoworld": "https://github.com/tredokiller/geoworld-unity.git#v1.9.1"
```

The repository is private: git on the computer must be signed in to GitHub (the same login that can clone it).

## Update

Change the version after `#` in `Packages/manifest.json` (`#v1.9.0` to `#v1.9.1`) and go back to Unity: it
downloads that version. Each version is listed in [CHANGELOG.md](CHANGELOG.md). (Without a version after `#` Unity
keeps the commit it first downloaded, recorded in `Packages/packages-lock.json`, so a version is the dependable way.)

## First steps in a project

1. **Tools > Geo World > Set up rendering for large worlds** (GPU Resident Drawer, occlusion culling).
2. **Tools > Geo World > Generator window** › **New area…**: find a place on the map of Ukraine (search, or pan and
   zoom), press Draw area and drag the area (drag it to move it and its corners to resize it; up to 100 km² is one export, up to
   1200 km² — 40 × 30 km — is extracted as parts with one origin and generated as one world), name it, and press
   Extract. The Building Extractor app, which comes with
   the package (with its own Python on Windows: nothing to install), fetches the buildings, roads, terrain and the
   radar damage estimate in the background; a small town takes a few minutes. The export is then selected in the
   window: choose a tiled world or one world and a few switches, and press Generate. Every other setting is under
   Advanced settings. **Config › New…** saves all of them (tree prefabs included) as a config asset; the window
   keeps every change in it and opens it again next time, and configs can be shared through the project. **Open full app** starts the app's web page (condition review, reports, news).

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
