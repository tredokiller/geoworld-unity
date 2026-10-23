# Changelog

## 1.9.0

- Fixed: rivers flickering and coming through each other. A river was a ribbon of its own along its line and OpenStreetMap's area of its water (water=river, waterway=riverbank, canals) a flat lake: where both were drawn the two surfaces crossed (the lake flat at one level for the whole area, the river falling along it), two rivers' ribbons lay over each other where they met, and at every corner of a wide river's line the inner side of its ribbon folded over itself. Water drawn twice at about one height flickers as the camera moves, and one surface came through the other. Now rivers and their areas are one surface on the terrain's grid with one level at every node (`GeoWater`): nothing is drawn twice, and tiles, which share the grid, meet exactly. A river's area follows its river (the level of the nearest point of a river's line through it, worked out exactly every 8 nodes of the world's grid and bilinear in between, so neighbouring tiles agree; a tile reads rivers 600 m beyond its margin for an area whose line lies outside it). Checked outside Unity with the generator's own code on a wide river with sharp corners, two rivers meeting, a 130 m river area, a pond on a stream and a ditch: water drawn twice went from up to 21 000 m² (600 m² where two rivers meet, 4 400 m² along the corners of one wide river) to none, and water missing under ground where the map has it from 3-26 % (72 % of a ditch) to 0.3 % or less.
- Fixed: a river's line through a lake (a pond on a stream, a reservoir) raised the lake's bed beside it to just above the river's water, a ridge of land through the lake. A river through a lake is now at the lake's level inside it (and goes back to its own level over 40 m), and never raises the lake's bed. The river surface leaves the lake's cells to the lake and meets it at the lake's level.
- Fixed: streams and ditches narrower than about a cell and a half were mostly ground a few millimetres over the water (which flickers and hides them). They are drawn and carved 1.5 cells wide (3 m on 1 km tiles of 513).
- The shore leaves the water's level at an angle (the bed and the shoulder outside), so ground and water cross along a line instead of lying within millimetres of each other over a band a metre or more wide, which flickered from afar along every shore (a quarter to three quarters less such ground in the checks above).
- Fixed: lakes misplaced north-south in a world in one piece whose export is not square (the lake's grid took the width for the depth). Tiles are square and were not affected.
- The satellite texture is crunched by default (Advanced settings > Satellite imagery > Compression: Crunched, Crunch quality 75): DXT1 with Crunch on top, about a quarter of the size in the world's assets and in a build (a 1 km tile's 2048 texture 2.7 MB to about 0.7-0.9 MB), the same DXT1 on the GPU once loaded. The satellite textures are most of a tiled world's size. DXT1 now uses the best encoding.
- The home and ruin libraries' colour textures are DXT1 with Crunch (quality 80) instead of BC7: about 0.7 MB each in a build instead of 5.3 MB (340 MB for all 63), and half the memory. Normal maps stay BC5.
- Addressables: the assets two or more tiles use (home and ruin models and textures, road and ground pictures, tree prefabs, the shaders of every material) go into one shared group of the world (`GeoTileAddressables.ShareDependencies`, after a tiled world is generated, before a build, and the Inspector's Share common assets). An asset in no group is copied by Addressables into every bundle that uses it: every tile's bundle had its own copy of the URP shaders and of the models it shows.
- Tools > Geo World > Build player of the open world... (`GeoBuild`, also in batch: `-executeMethod GeoWorld.Editor.GeoBuild.Batch -geoScene <main scene> -geoOut <player.exe>`): builds Addressables content and only the main scene into the player for a world with Addressable Scenes (`GeoBenchmarks.BuildPlayer` put every tile scene in as well, so the tiles were in the build twice), compresses the player's data with LZ4HC, and writes GeoBuildReport.txt: tile bundles (median and largest), the shared bundle, Resources, the player's data, and what the world's assets weigh in the project. Tools > Geo World > Report the open world's size gives the second part without a build.
- New area takes areas up to 1200 km² (40 x 30 km): above 100 km² an area is extracted as the fewest parts of at most 100 km² and 20 km a side (a 40 x 20 km area is 3 x 3 parts), overlapping by 100 m, one after the other, all with the area's middle as the fixed origin, into `<name>_parts/part_N` (parts already there are kept, so a stopped extraction goes on); Generate one world from the parts makes one tiled world of them (`GeoExtractor.ExtractParts`).
- Tools > Geo World > Fly across the open world / Fly over the whole open world (play mode): a flight while the tiles stream that reports the frame rate's median, mean, 1 % and 0.1 % lows and spread, the slow frames, the checks at which a tile near the camera was not loaded yet, and how long after its tile loaded Nature Renderer started drawing each terrain's trees (with the terrains it had not started on 5 s after). `GeoBenchmark.Request.flyPath` flies a path of several legs, also in a player.
- Nature Renderer: a tile whose tree data is missing no longer holds its Nature Renderer back forever (the loader logs it and lets it start); the report names tree prefab materials without Enable GPU Instancing, whose trees a player build leaves undrawn (the instancing variants Nature Renderer draws with are stripped).
- Load radius of a tiled world as a slider of 1 to 10 km (2 km by default): Geo World window > Advanced settings > Tiles and streaming (`GenerationSettings.loadRadius`, given to the streamer when the world is generated), the Geo Tiled World's Inspector and the tile streamer's. Below it, how many tiles it keeps loaded around the camera (2 km: 21-24 tiles of 1 km, 5 km about 100, 10 km about 360), with a warning above 120. Changing it rebuilds no tile. A tile is unloaded at least 600 m beyond the load radius (with a large load radius and the old unload radius of 2.6 km, tiles at the edge were loaded and unloaded over and over).
- Tiles are rebuilt (tile version 18): Rebuild changed tiles.
- Tests: six new for the water (never drawn twice at bends and where rivers meet, a river's area following its river, a river through a lake, a narrow stream, two tiles meeting where a river's line is outside one), one for the satellite compression, one for cutting a large area into parts, one for Nature Renderer with missing tree data, one for the load radius.

## 1.8.2

- Fixed: a wall at the end of a lake's shore. The ground within a few metres of the shore was cut down to a gentle slope and beyond that its own height came back all at once, so a bank higher than about 3 m ended in a wall of its height (13 m for a bank of 20 m). Now the ground goes back to its own height over a distance that grows with the difference (10 cells and 2.5 m for every metre it differs, 20 to 100 m): no step is steeper than a bank.
- Fixed: the edge of the water hanging in the air, which showed as steps and squares along the shore. The shore only brought ground down, so on sloping ground the surface (which reaches a cell or two beyond the outline) was over land lower than the water: 2.4 m of air under its edge on a slope of 4 %. Low land next to the shore is now raised to a 25 cm shoulder above the water, so the edge of the water is always under ground, and it goes back to its own height over the same distance.
- Fixed: exports of one world with different zeros. The extractor read the ground level at the origin from the elevation grid of each export, and a point outside a grid takes the height of the nearest edge: every export whose area did not hold the origin (most parts of a city exported in pieces) had a zero of its own for all its heights, which is a wall of the difference wherever two exports meet. The extractor now reads the ground at the origin from a small grid of its own (`Dem.ground_at`, in the app and in Extractor~). Exports made with this version agree; older exports are still brought to one datum when a world is generated (1.8.1).
- Fixed: the water of a river not meeting the ground. A river's bed was carved with a narrow, steep falloff, so at the edge of its ribbon the ground was still about the whole depth below the water: in the Chuhuiv sample the edge hung in the air over the bed in all 396 sections of the Siverskyi Donets (1.5 m of air), with a dry trench between the water and the bank. A river is now shaped like a thin lake (`GeoShore`, shared with the lakes) from its distance to the edge of its water: the bed falls from the water level, the ground outside never lies lower than the water and goes back to its own height without a wall, and the river's surface goes a little under the banks, so its edge is under ground. The bed is worked out from the nearest piece of river at every node, so tiles agree at their edge.
- Fixed: rivers much narrower than the real river, and beside its middle, which made the water look missing where the satellite picture shows a wide river. OpenStreetMap draws a river as a line and the export gives it the width of its kind (14 m for any river, 2 m for a stream). The generator now reads the ground map of the export (the water class, ESA WorldCover with the OSM water polygons burned in): where water lies within 12 m of the line, the river gets that width at every sample (a median over 24 m, so a bridge or a tree does not change it; it widens by at most 0.5 m for each metre along it) and its line moves to the middle of that water. Lakes, river mouths (water 90 m or more across) and rivers narrower than the map keep the width they have. `PathBuild.widths` carries the widths into tiles; the ground under a river is painted by them too.
- Fixed: the trees of a tile that is switched off and on again (a game that culls tiles, or a scene that disables them) were gone for good where Nature Renderer draws them. The tree loader destroyed the tile's own copy of the terrain data when the tile was switched off, and Nature Renderer holds on to the data it first drew from: it came back to "the object has been destroyed" and drew nothing (MissingReferenceException from TerrainGrassRenderer in the console). Switching Nature Renderer off and on did not help, only its Restart did. The loader now keeps the tile's trees while it is only switched off and lets them go when the tile is destroyed (a tile that is unloaded, as before). No tile needs rebuilding.
- Nature Renderer's delayed start (GeoNatureRendererStart) now holds it back every time the tile is switched on, not only when it first loads, so that it never starts on the empty terrain data of the moment; it is told the terrain and its data when it starts, and restarted if the tile's trees arrive after it was already running. Frame numbers are reset with every Play mode (with Enter Play Mode without a domain reload, Nature Renderer waited for the frame count to pass the last Play mode's).
- Checked with the real Nature Renderer on a 16-tile world of 2 km tiles (340 000 trees, Lyman): flying the length of the world and back at 200 m, every tree within the terrain's tree distance (1 500 m) is drawn on every stop, on a first load and after tiles were unloaded and loaded again; Nature Renderer is drawing 3 to 34 frames after a tile loads. Beyond 1 500 m trees are not drawn by design (Terrain Tree Distance, the tree prefab's LOD cull).
- Tests: three new for the tree loader and Nature Renderer's start (a stand-in for its component, in a test-only assembly outside the Editor folders).
- The level of a river's water is the mean of the ground under its line over 40 m (it wobbled with the noise of the elevation data); the ground is carved to it.
- Tiles are rebuilt (tile version 17): Rebuild changed tiles.
- Tests: five new for the shore of a lake and thirteen for rivers (the edge under the bank, no wall, widths, the ground map, taper, level, tile edge); seven for the origin ground in the app's tests (tests/test_origin_ground.py).

## 1.8.1

- Fixed: a wall along the seam between exports. Each export's heightmap was cut from the elevation data for its own area, so where two exports meet their heights differ (by a little, or by a whole datum when the ground level at the origin differs) and every tile took its own export's numbers. Now the ground is one function of the point for the whole world: the exports are first brought to one datum (each is moved by the median difference to the earlier export it overlaps, over the overlap), and across the overlap the heights of the exports that reach a point are averaged, weighted by how far inside its bounds the point is plus a 24 m feather, so the ground goes over from one export to the next instead of stepping. Away from a seam a point has one export's own height. Every tile gets the same height at a point whichever export builds it. The height scale (Terrain Base Y and Range) is the world's, not each export's.
- Fixed: lakes drawn as squares with gaps against the terrain. The surface was a grid of squares laid from each tile's own corner, and a square was kept only when its four corners and its middle were inside the outline: the shore was stepped, the water stopped short of it, a pit of the full depth showed between the water and the terrain, and the squares of two tiles did not line up. Now the bed slopes from the shore (at the water level) down to the depth over a bank of 4 to 14 m, the ground just outside the shore comes down to the water level on a gentle slope, and the surface is flat quads on the terrain's own height grid (which every tile shares), one for each run of wet cells in a row: the outline is followed to the grid's resolution, the cells the outline touches are wet, and the edge of the first of them is under the bank. Surfaces of two tiles meet exactly.
- One lake from several exports: the outlines that two exports have of one lake (the same id) are one surface at one level, taken from the first export that has it, and a lake across a seam has one level on both sides.
- Tiles are rebuilt (tile version 16): Rebuild changed tiles.
- Tests: three new (the bank and the surface of a lake, two tiles' surfaces meeting, two exports with a 3 m different datum and a lake across the seam).

## 1.8.0

- Fixed: the parts of one area (Kharkiv as six exports) were cut into tiles as separate worlds, each on a grid that began at its own export's lowest corner, with full 2 km tiles at its edges. Tiles of neighbouring parts did not line up and overlapped by hundreds of metres, up to 1.9 km between the north and south groups.
- One grid for every export: tile (i, j) is the square [i x size, (i + 1) x size) x [j x size, (j + 1) x size) of the export's own coordinates, whatever its bounds are. A tile has the same index, place and name in every export. A world made before this version (its grid began at the export's corner) is rebuilt in full.
- One world from several exports: Tools > Geo World > Generate one world from several exports... (also the Several exports... button of the Geo World window, `GeoTiles.Generate(parts, ...)`, `GeoDefaults.Generate(folder)` and `-geoExport <folder of parts>` in batch mode). Parts must be exported with the same fixed origin; different origins are refused with a message.
- Who owns the border: the exports are listed in order and each owns the ground of its bounds that no earlier one owns (a rectangle: its region). Regions never overlap, so no ground is built twice. A tile is built once. In the middle of an export it is that export's alone; where exports meet it is built from all of them: each gives the ground (height), roads, rivers, buildings, trees and props inside its own region, and roads are cut at the seam between the regions. The tile records the exports it came from (Tile > Source) and its root has Tile Parts.
- A tile built from several exports is rebuilt from the Geo Tiled World object (Rebuild changed tiles / Rebuild all tiles), which now lists the exports (Parts). The tile's own Rebuild refuses it with a message.
- Tiles that no export reaches any more (an export removed or changed) are deleted with their scenes and assets; the grid no longer depends on the first export, so a part can be added later without moving any tile.
- Ground classes (the material map) and the canopy of a tile built from several exports come from the export that holds most of it. Water, areas, fences and hedges that cross the seam are drawn by both exports (twice over their overlap).
- Tests: six new (the shared grid, the owner of every point, slivers of tiles, two exports as one world, different origins) and the riverside tiled sample, which now has 4 x 2 tiles because its grid is shared (it was 3 x 2 on its own corner).

## 1.7.0

- Tile generation exports terrain tree instances and instanced tree batches to Resources/TerrainChunker/TreeData, with stable world/tile keys and asset GUIDs.
- Geo World restores external trees asynchronously in batches, releases runtime tree data on unload, and starts Nature Renderer after trees are ready.
- UniTask scene streaming supports Addressables and built-in scenes, staggered activation, retries, and cleanup when the streamer stops.
- New tiled worlds register each tile as a separate Addressables bundle. Existing worlds retain built-in streaming; enable Addressable Scenes and rebuild changed tiles to migrate.
- Requires Addressables 2.9.1 and UniTask 2.5.10. Install UniTask via its Git package URL before updating Geo World, then rebuild tiles and Addressables content. External trees are restored in Play mode and are not shown in Edit mode.
- Validated with three passing Unity tests covering tree serialization, stable asset regeneration, and repeated scene load/unload with tree restoration.

## 1.6.1

- Nature Renderer starts on a terrain two frames after its tile is switched on, one terrain at a time (worst tile frame with it 154-257 ms to 102-140 ms); worlds made with Nature Renderer before: Rebuild all tiles
- The tile streamer switches loaded tiles on one at a time, the nearest first, Frames Between Tiles apart (3), so two tiles no longer land in the same frame

## 1.6.0

- Tiles load without editor freezes: a tiled world draws its ground roads one mesh per chunk and material (a Kharkiv tile 1 687 objects to 502; worst stall in a flight 962 ms to 163 ms); bridges, raised roads and rivers keep their objects
- Generation configs: Config > New... in the Geo World window saves every setting, tree prefabs included, as an asset that the window keeps editing and opens again next time; configs can be shared through the project
- Generate a tiled world again to rebuild its tiles with this version

## 1.5.1

- New area: drag the area to move it (its size on the ground is kept); right or middle drag pans the map anywhere

## 1.5.0

- Tree prefabs (Advanced settings > Trees): your own trees, each scaled to its mapped or generated height; the terrain's own trees in a merged world (drawn by Unity's terrain, paintable with its tree brush), a copy per tree in an editable one; drop prefabs on the list
- Trees with Nature Renderer: a switch shown only where the project has Nature Renderer (Visual Design Cafe, not included); it then draws the trees with its GPU culling and LOD, and the report names materials of your prefabs it cannot draw
- New area: drag the corners of the area to resize it; an area is at most 100 km² of any shape; Draw area is a blue button
- Generate a world again to rebuild its tiles with this version

## 1.4.2

- The terrain casts no shadows by default: 3-6 ms less a frame in a city, no visible change (buildings and trees still cast theirs on the ground)
- Terrain casts shadows in the Advanced settings turns them back on, for mountains
- Generate a world again to rebuild its tiles with this version

## 1.4.1

- Tiled cities load without lag: Auto building mode merges buildings and props in every tile (a 1 km tile of Kharkiv went from 15 000 objects to 1 700 and no longer runs the editor out of video memory)
- Road crossings are one mesh per chunk and material instead of an object per piece; roads keep their own objects for editing
- Generate a tiled world again to rebuild its tiles with this version

## 1.4.0

- New area... in the Geo World window: draw an area on a map of Ukraine and extract it with the Building Extractor app, which comes with the package (with its own Python on Windows); the new export is selected, ready to generate
- Open full app starts the app's web page (condition review, reports, news)

## 1.3.0

- A simpler, nicer Geo World window: pick an export with a picture of the area, tiled or one world, four switches, Advanced settings for the rest
- Update notifications: console, Scene view and an Update button in the window when a newer version exists

## 1.2.0

- Tools > Geo World holds only Generate world from export..., Generator window, Set up rendering for large worlds and Update Geo World...
- Update Geo World... updates the project to the newest version; the console says when there is one
- Tools for developing the package are in Tools > Geo World > Developer, only where GEOWORLD_DEVELOPMENT is defined

## 1.1.0

- The 25 ruin and 38 home models come with the package: nothing to import in a project
- Model textures six times smaller (colour JPEG, normal maps 1024 px PNG): the package is 277 MB
- Home normal maps are imported as normal maps (apartment walls had dark blotches)

## 1.0.0

First version as a package.

- Tools > Geo World > Generate world from export... builds a tiled satellite world of any exported area.
- Satellite imagery sized to the area, roads with soft edges, ruin and home models from the app's building condition.
- Tiles stream without freezes: homes are made from a list after a tile loads.
- Ruin and home models are imported per project from folders remembered on the computer.
