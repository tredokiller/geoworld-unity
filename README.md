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

## Land cover

A tiled world knows what its ground is (forest, orchard, cropland, grass, built-up ground, water...) from its export's
ground map, ESA WorldCover 2021 with OpenStreetMap's land use, roads and buildings over it, on a grid of 10 m. A game
asks it anywhere, whether the tile there is loaded or not and across the tiles' edges:

```csharp
if (GeoLandCover.TrySample(position, out GeoLandClass ground) && ground == GeoLandClass.Orchard) { /* ... */ }

var shares = new float[GeoLandCover.ClassCount];
if (GeoLandCover.TryGetFractions(position, 120, shares))
    treeCover = GeoLandCover.Share(shares, GeoLandClass.Forest, GeoLandClass.Orchard);
```

Generating tiles bakes it (one small asset a tile in `<world folder>/Land cover`, `GeoLandCover` on the world's object);
for a world made earlier, press **Bake land cover** in the Geo Tiled World's Inspector. No tile is rebuilt.

With land cover the tiles' trees are as tall as what they stand in when they load (Tree Heights on `GeoLandCover`):
woods 15-24 m, shelterbelts and tree rows 8-14 m, orchards 4-7 m, villages 6-12 m, each tree on the prefab whose own
height is nearest.

## World map data for the environment layer

The environment layer (`Environment`, assembly `GeoWorld.Environment`) paints road surfaces and keeps grass off roads,
draws field rows and picks tree species by leaf type from data of the world's own map: roads with their width and
surface, farmland outlines with the bearing of their rows, forest outlines with their leaf type, all from the exports'
`environment.json`. They are baked for each world into `<world folder>/Environment` (Road areas, Field areas, Tree leaf
areas) and found through `WorldMapData` on the world's object, next to its land cover: after the tiles are generated,
with **Bake roads, fields and leaf types** in that component's Inspector, or **Tools > Environment > Bake World Map Data**.
A world without them has no road surfaces, field rows or leaf types; it never takes another world's.

The layer itself is one component, `WorldEnvironment`, which the bake puts on the world's object too: when it wakes it
adds every system the layer has — tree species by place, grass, flowers, leaf litter, dead wood and understory on the
tiles, ground detail and forest floor, road surfaces, wet ground, puddles and marsh, water sky and shore, aerial haze and
cloud shadows, seasons with snow, colour and sound, rain, and wind for plants and water — so a project gets the same
world without code of its own. It needs Enviro 3 in the scene. A game sets the wind with `WorldWind.Set`, the season with
`Seasons.Apply` and the distances on `NatureRendererSettings`, `ShadowRange` and `GroundDressing`.

Worlds a player generates (world packages) look the same: the package carries the land cover and the map data, baked from
the export while it is generated (with the editor bake's satellite step: each tile's satellite image moves the roads onto
the road it shows and gives the fields their rows' bearing), and its tiles' trees become Nature Renderer terrain trees with
species by place when they load. Open the package on an object (`GeoWorldPackageStreamer` does it) and keep `WorldEnvironment` in the scene.

## Battlefields

An area after a finished battle (no fire, smoke or artillery: what is left): **Bake battlefield** in the Geo Tiled World's
Inspector, then **Rebuild changed tiles**. The bake reads each export once and writes `combat.json` next to it: craters
found on the satellite imagery (from about 2 m across to bomb craters, fresh ones and ones with water), the small pits no
imagery resolves scattered where the fighting was, trenches dug in the style of the war along tree lines under fire (never
traced from imagery), and an estimated state for the buildings the app has no evidence for. The tiles carve the craters
into their ground and draw the trenches in 3D (walls, floor, lip and spoil banks as meshes with the terrain's own look, the
terrain cut under them); the environment layer draws the craters too small for the height grid, fills ponds, keeps grass
out of fresh bowls and off the trenches, leaves the fields there abandoned and damages the trees. Destroyed buildings are drawn with thick
broken walls and lumpy rubble. A world package gets the same with `GeoWorldRequest.battlefield`. Reading data out of the
imagery needs a licence that allows it (Esri World Imagery's terms do not for this use; `GeoCombatSettings.detect` off
keeps only the scattered pits). See BATTLEFIELDS in the manual.

## Install in a project

**Fast updates (recommended): a git submodule in `Packages`.** Every update then downloads and imports only the files that
changed (a few MB instead of the whole package), and the project's commit of an update is one line. Needs git with
[Git LFS](https://git-lfs.com) (`git lfs install` once). In the project's folder:

```
git submodule add https://github.com/tredokiller/geoworld-unity.git Packages/com.tredokiller.geoworld
git -C Packages/com.tredokiller.geoworld checkout v1.19.0
git -C Packages/com.tredokiller.geoworld lfs pull
git config -f .gitmodules submodule.Packages/com.tredokiller.geoworld.shallow true
```

Commit `.gitmodules` and `Packages/com.tredokiller.geoworld`; the others get it with `git submodule update --init`. (A project
that is not a git repository: `git clone` into the same folder.) Unity takes a package in `Packages` without an entry in
`Packages/manifest.json`.

**Through the Package Manager:** Package Manager > **+** > **Add package from git URL...** and enter the version you want:

```
https://github.com/tredokiller/geoworld-unity.git#v1.19.0
```

or add it to `Packages/manifest.json`:

```json
"com.tredokiller.geoworld": "https://github.com/tredokiller/geoworld-unity.git#v1.19.0"
```

The Package Manager downloads the whole package (some 300 MB, and its Git LFS files) for every version, and Unity imports all
of it again. The first **Update** offers to switch to fast updates.

The repository is private: git on the computer must be signed in to GitHub (the same login that can clone it).

## Update

**Update** in the Geo World window (or Tools > Geo World > Update Geo World...). With fast updates it fetches the new
version's tag and checks it out in place, with a bar of git's progress; Unity imports what changed and recompiles. By hand:
`git -C Packages/com.tredokiller.geoworld fetch --depth 1 origin tag v1.19.0` and `checkout v1.19.0`, then `lfs pull`.
Through the Package Manager: change the version after `#` in `Packages/manifest.json` and go back to Unity. Each version is
listed in [CHANGELOG.md](CHANGELOG.md).

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

## Generating worlds in a built player

A player can make worlds itself, without the editor: **Tools > Geo World > Runtime generation > Prepare runtime assets** once (and
after updating the package), then in code `await GeoWorldGenerator.GenerateAsync(request, progress, cancellation)` (or
`GeoWorldGeneratorHost.GenerateAsync` to run it in a second process of the player and keep the game's frames) returns a world package
on disk, whose tiles load with `package.LoadTileAsync(index, parent)` and unload with the handle. A standalone build gets the Building
Extractor app in `StreamingAssets/GeoWorld/Extractor`. **Create demo scene** / **Build demo player...** under the same menu make a player
to try it. See RUNTIME GENERATION in the manual for the package format, navigation and limits.

## Developing the package

It lives in `terrain-generator/Packages/com.tredokiller.geoworld` of the Building Extractor repository, where the
Unity project uses it directly. That project defines `GEOWORLD_DEVELOPMENT`, which adds **Tools > Geo World >
Developer**: **Import ruin models** / **Import home models** rebuild the package's libraries from `ruins/models` and
`intact/models` (they ask once for the folders and remember them), **Reduce library models again**, and the benchmark.
`py -3 tools/publish_unity_package.py <version> "<what changed>"` publishes a version for the other projects.

## More

The manual is in [Documentation~/Manual.txt](Documentation~/Manual.txt): settings, tiled worlds and streaming,
satellite imagery, building condition, ruin and home models, editing roads and buildings.
