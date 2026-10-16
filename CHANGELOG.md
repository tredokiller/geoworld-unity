# Changelog

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
