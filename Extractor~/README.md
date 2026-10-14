# Building Extractor

A standalone local app. Draw an area on a satellite map of Ukraine and it finds
every building in that area, with position, size, rotation and height. It can also
read the ground around them: the shape of the terrain, roads, water, fields, forest and
the ground material at every spot. You can check the result on the map, then export it
for Unity or Godot.

No dependencies: Python 3.9+ standard library only. You need an internet connection.

## Run

On Windows, double-click `start.bat`, or:

```bash
py -3 server.py --open
```

On macOS / Linux: `python3 server.py --open`.

The app opens at http://localhost:8765.

### Lyman photo references

The **Photo references** panel ships with 15 downloaded historical photographs
of Lyman and 46 visually reviewed observations. Use **Explore Lyman**, then open
a purple numbered point or a thumbnail. Photos work from the local bundle;
the basemap and new area extractions still need the internet.

These are individual Wikimedia Commons photographs, **not continuous street-view
panoramas**. The checked Panoramax area and five KartaView searches had no photos;
Mapillary's Ukraine restriction is documented in **Imagery coverage & dates**.
Each image retains its source, author and individual CC BY or CC BY-SA license. Camera dates, original
upload dates and Commons archival dates are kept separate. Historical appearances
are not a statement about present conditions.

Observations describe facade materials/colours, windows, entrances, paving, fences
and vegetation. They are AI-assisted visual reviews of the bundled images, not an
automatic image-analysis service. Colours are approximate; an unseen roof stays
unknown. Special shapes such as church domes still need custom models.

- **Link a building** selects the actual footprint in an open building export.
  Linked colour/roof choices become `visualDetails` in the building JSON. Imported
  geometry, heights and floor counts are preserved; observed floors stay evidence.
- **Place on map** assigns a supported point prop its own position. Photo geotags
  are never used as object positions. Fences, surfaces and vegetation remain style
  references until their geometry is separately mapped.
- **Create enriched copy** saves a new export with reviewed links and the complete
  photo pack. New Lyman extractions include applicable saved links automatically.
  Clear a link and create a new copy to remove its previous application.
- **Download photo pack** includes images, observations and attribution for use
  outside the app. User edits persist in `cache/street_reference/lyman/` and do not
  overwrite the bundled catalog in `data/street_reference/lyman/`.

The companion Unity generator consumes linked colours and flat/gabled/hipped roof
choices on initial import, retains photo evidence, and preserves subsequent edits.
Descriptive details such as window style or entrance ornamentation are references
for choosing/editing assets, rather than automatically reconstructed geometry.

To validate the bundled assets offline, run
`python tools/import_lyman_references.py --validate-only`. To refresh the five
known public photo records and coverage checks, add `--refresh-metadata --check-coverage`
instead. The importer preserves observations and never bypasses restricted providers.

### Roof colours and building appearance

New OSM extractions preserve optional roof shape, colour, material, height, slope
direction and orientation, plus wall colour/material and architectural tags in
`mappedAppearance`. Raw values, OSM source links and ODbL attribution travel with
the export. Missing tags remain missing in `mappedAppearance`; the separate `roof`
block (see "Roofs") merges mapped, reviewed and clearly flagged estimated roof
values. The map popup separates mapped details from reviewed photo details. Unity applies supported mapped fields, then supplied
reviewed fields, while preserving existing user edits. Roof height is part of
total building height. Unsupported shapes retain their description and require a
custom model; materials are metadata rather than automatically created textures.

**Import overhead image** accepts a local north-up **Web Mercator (EPSG:3857)**
PNG/JPEG, its geographic bounds, optional capture date, attribution and a rights
declaration. Reproject other image coordinate systems before importing. Images
are limited to 12 MB and 25 megapixels. Display the image on the map and verify
alignment, then choose **Suggest roof colour** and click a building footprint.
The app uses median interior pixel colours, excludes supplied courtyard holes,
and abstains for coarse, tiny, clipped or mostly unusable samples. It requires
1 m/pixel or finer imagery; upscaling a coarse image adds no real detail.

Review shadows, vegetation, roof displacement and lighting before **Save roof
colour**. The colour is an approximate inferred observation with source evidence.
Roof shape is reviewed manually; RGB colour alone does not identify shape or
material. **Create enriched copy** packages accepted observations and source
images for Unity. This workflow reads only the imported image, not basemap tiles.
Older exports may lack courtyard geometry; their sampling review shows a warning.
New OSM exports preserve inner rings for map display and sampling; Unity's existing
simple building meshes still do not reconstruct courtyards.

### Building condition (war damage)

The **Building condition** panel records what state each building is in, so a generated world can show war
damage, for any place in Ukraine. Which buildings are damaged is decided here, in the app, and nowhere else: the
Unity generator draws the condition of the export as it is. Every piece of evidence has a state (intact, damaged, heavily damaged, destroyed), the date it applies
to, a basis (observed, reported, mapped, estimated), a confidence and a source. A building without evidence is
**unknown** and stays unknown: it is never assumed intact or damaged. Where several pieces of evidence meet,
reported, reviewed and mapped ones outrank estimates, and the worst state wins among equals (damage does not
heal during a war).

- **Reports.** *Reports & sources* adds a news or humanitarian report (title, link, date). In *Review mode* click
  the building it names, choose its state and the source: it becomes evidence for that building. Facts only; keep
  troop positions and operations out. News photos are copyrighted: link them, do not bundle them.
- **News headlines.** *Reports & sources → Fetch recent headlines* lists the last two weeks of headlines that match the
  area's search (`newsQuery` in the catalog; GDELT DOC API, free, no key). Only the title, outlet, date and link are
  kept (`cache/condition/<area>/news.json`), and nothing in a headline is applied to a building: add the useful ones
  as a report and link it to a building in Review mode. GDELT answers about once every few seconds and sometimes not
  at all; the fetch waits and retries, then shows the last list.
- **Reviews.** Look at imagery or a photo yourself and set a building's state the same way (*My own review*).
  The app does not analyse the satellite basemap; you do.
- **OpenStreetMap.** `building=ruins` (and `ruins=yes`, lifecycle tags, `damage=yes`) count as mapped evidence.
- **Radar estimate.** Sentinel-1 radar (open data, read through Microsoft Planetary Computer, no account) shows where
  the echo of the last weeks differs from the year before the invasion. *Estimate the damage from radar* in the
  extraction options (on by default) runs it with every extraction; *Estimate damage from radar* in the panel runs it
  for the open export and makes a new package with the condition; `py -3 tools/sar_damage.py --export <file>` does
  the same from the command line. The first run for a place reads a few hundred MB and takes about ten minutes for a
  town (up to 250 km² at a time); an estimate from the last week is reused, and the newest scene is days old.
  It is a pixel-wise t-test after Ballinger et al. 2024. A pixel is 10 m: this is an estimate with low confidence,
  informative for large buildings and districts and weak for a single house. Scenes are compared on the UTM grid
  they are stored in, a scene that covers only part of the area counts where it does, and the orbit is one with
  enough scenes both before the invasion and in the last weeks.
- **Calibration.** Radar ranks buildings; published statistics say how many are damaged. `py -3
  tools/calibrate_condition.py --area <area> --export <file>` sets the radar score lines from the rules in
  `data/condition/<area>/catalog.json` (for Lyman: over 80% of the buildings damaged or destroyed, 15 of 236
  apartment blocks beyond repair), so the radar decides *which* buildings and the reports *how many*. The lines and
  the statistics they come from are stored in the catalog. An area without such figures uses the general lines
  (|t| 3.3 damaged, 6 heavily damaged, 9 destroyed), and every estimate there says it is not calibrated.

*Colour by Condition* shows the result on the map (dashed outlines are radar estimates). The evidence is kept per
area: `data/condition/<area>/` for the curated areas that come with the app (Lyman), and `cache/condition/<area>/`
for what you add in the app and for the areas the app makes for a radar estimate (`auto-...`, from the export's
bounds plus about 200 m). An export uses the area that covers it best; extractions apply it automatically, and
**Create package with condition** makes a new export with the current evidence. A place no area covers has no
condition until its damage is estimated from radar: its buildings are unknown, and Unity draws them whole. The
package adds `condition.json` (sources, dates, licenses, assessments), `CONDITION-CREDITS.txt` and the radar preview.
The Unity generator (companion project, **Tools > Geo World > Generate world from export...**) draws every building
the package gives as damaged, heavily damaged or destroyed as damaged, and every other one whole (see
the manual of the Unity package, `terrain-generator/Packages/com.tredokiller.geoworld/Documentation~/Manual.txt`; the package is published for other Unity projects with `py -3 tools/publish_unity_package.py <version>`).

### Lyman source research on 6 October 2026

The strongest immediately usable additions are the expanded historical photo
library and better preservation of mapped attributes. A free high-resolution
overhead image with verified Lyman coverage and rights for derived Unity data was
not found in the checked sources.

| Source | Verified finding | Use in this app |
|---|---|---|
| OSM existing sample | All 285 ways returned by the official read API; 163 have floor counts, none have roof shape/colour/material or wall colour/material. This covers only the 1.938 km² sample. | Preserve available tags in `mappedAppearance`; never present a guess as mapped. Typical-roof estimates go in the separate `roof` block, flagged `estimated`, and can be turned off. Raw audit is in `cache/lyman-research/osm-sample-audit.json`. |
| Wikimedia Commons | 15 licensed historical photos, including 10 additional facades, roofs and streetscape references. Five have reference geotags; ten have no verified coordinates. | Local gallery and 46 reviewed observations; matching a building or placing a prop remains explicit. |
| Esri World Imagery | The metadata query at 48.98013 N, 37.8168 E reports WorldView-2 imagery captured 29 September 2023, 0.5 m source resolution, 0.6 m sample resolution and 5 m stated accuracy. This is a point result, not a citywide capture-date claim. | Useful viewing detail, but the published automated-extraction permission limits derived data to non-commercial use within ArcGIS. It does not authorize this standalone Unity pipeline. [Official extraction terms](https://www.esri.com/arcgis-blog/products/arcgis-living-atlas/imagery/learn-to-use-ai-to-extract-information-from-world-imagery). |
| Sentinel-2 | Native visible bands are 10 m per pixel. | Suitable for broad land cover; too coarse for dependable colour or type of ordinary house roofs (an inference from resolution). [Copernicus band documentation](https://documentation.dataspace.copernicus.eu/APIs/SentinelHub/Data/S2L2A.html). |
| Overture Buildings | Its schema includes optional roof shape, colour, material and direction. Actual filled Lyman coverage was not verified. | A possible additional vector source, not evidence that missing local roof attributes exist. [Building schema](https://docs.overturemaps.org/schema/v1.18.0/reference/buildings/building/). |
| Maxar Open Data | No Ukraine activation was found in the 55 checked event collections; the registry states CC BY-NC 4.0. | No usable Lyman roof image identified. [Registry and license](https://registry.opendata.aws/maxar-open-data/). |
| NASA astronaut photos | Three apparent nearby coordinate matches have uncataloged image centers; their coordinates describe the spacecraft nadir. Visual inspection shows regional terrain or night horizons, not resolvable Lyman roofs. | Rejected as local roof sources. [NASA coordinate guidance](https://eol.jsc.nasa.gov/FAQ/). |

For higher quality geometry, the next useful input is a licensed, dated,
orthorectified aerial or satellite image with substantially sub-metre detail and
good alignment. Roof colour can be reviewed from such imagery; reliable roof
planes and slopes usually need stronger geometric evidence such as stereo or a
surface model, rather than a single colour sample. The implemented workflow keeps
those uncertainties visible instead of silently turning them into building facts.

## Extract an area

1. **Find a place.** Search for a city or village, or type coordinates such as `49.834, 36.685`.
2. **Select an area.** Drag to draw a rectangle. Drag a corner to resize it or the centre to move it.
3. **Options.** Pick the data sources, whether to build the terrain and ground, and the Unity world origin.
4. **Preview area.** Everything is drawn on the map. Check it:
   - *Outlines*: the real building shapes.
   - *Unity boxes*: the oriented box (width × length × rotation) that a box/prefab gets placed in.
   - *Both*: the outlines with the boxes dashed on top.
   - Colour by type, source (OSM vs satellite ML) or height. Click a building for its details.
   - *Terrain & ground*: switch the ground-material picture, terrain shading, roads, water and trees on and off.
     Click a road to see its class, width and surface.
5. **Download.** *Buildings JSON* is the building list. *Terrain & ground (.zip)* is the heightmap, the
   material map and the roads, water, fields and trees. Copies are also saved in `exports/`.

## Where the data comes from

| Data | Source | Notes |
|---|---|---|
| Buildings | OpenStreetMap (Overpass API) | Outlines traced by people from imagery. Has building type, floors and sometimes height |
| Buildings | Microsoft Global ML Building Footprints | Outlines detected from satellite imagery by a neural network. Covers buildings OSM is missing (sheds, outbuildings, villages) |
| Terrain shape | Copernicus DEM GLO-30 | 1 arc-second (about 30 m) grid. A surface model: it reads a few metres high over forests and dense buildings |
| Land cover | ESA WorldCover 2021 | 10 m grid: tree cover, shrubland, grassland, cropland, built-up, bare, water, wetland |
| Roads, railways, water, fields, parks, tree rows | OpenStreetMap | Painted over the land cover wherever people have mapped them |

When both building sources are on, OSM wins. An ML outline that overlaps an OSM building is dropped as a duplicate.

**Overlaps and roads.** The two sources disagree by a few metres, so outlines overlap and ML outlines slide onto
streets. With *Fix overlapping outlines and buildings on roads* (on by default; `--keep-conflicts` turns it off)
every remaining overlap is resolved: OSM wins over ML and the larger outline over the smaller, and the other one is
cut along the winner's wall, or dropped when the winner covers half of it (a duplicate, or a building part mapped
inside another building). An ML outline reaching onto an OSM road is moved straight back off it (up to 3.5 m, keeping
1 m from the edge for eaves and porches), cut at the road edge where moving would hit something, and dropped when half
of it lies on the road. Roads count at their width (tag, lanes or the class default; an unpaved road without a width
tag is a 3.5 m lane, not a town street of its class, here and in the Terrain & ground package); footways, bridges and
tunnels do not count. OSM
buildings on a road are left as mapped. The roads come with the OSM buildings in one request. Each changed building
lists what happened in `footprintAdjusted`, its map popup says it, and the export's `conflicts` block counts it all.

Height comes from the OSM `height` tag, or else `building:levels` × 3 m, or else
a default for the building type (see `extractor/classify.py`). Microsoft's
Ukraine data has no heights. For buildings without a type (all ML buildings and
OSM `building=yes`), the type is guessed from footprint size and shape. These
are marked `"categorySource": "guessed"`.

Everything downloaded is cached in `cache/`. Microsoft tiles are 2 to 40 MB each. Terrain
comes in blocks of about 2.7 MB that cover roughly 30 × 20 km, so an area next to one you
already extracted costs nothing. The public Overpass servers are often overloaded. The app
tries several in turn. If OSM is completely down, it continues with what it has and shows a warning.

## Large areas / scripting

```bash
py -3 cli.py --bbox 49.82,36.66,49.85,36.71 --out area.json
py -3 cli.py --polygon area.geojson --sources osm --origin 49.8335,36.6866 --out area.json
py -3 cli.py --bbox 49.82,36.66,49.85,36.71 --out area.json --environment area_terrain
```

`--no-roof-estimates` keeps only mapped and reviewed roof data (see "Roofs").
`--environment DIR` also writes the terrain and ground package into `DIR` and `DIR.zip`.
Add `--no-heightmap` or `--no-ground` to leave a part out, `--height-cell 2` for a finer
heightmap and `--ground-cell 2` for a coarser material map.

## Export format (`geo-buildings`, version 1)

```jsonc
{
  "format": "geo-buildings",
  "version": 1,
  "projection": "local-enu-wgs84",       // see "Coordinates" below
  "origin": { "lat": 49.834, "lon": 36.685 },
  "area": { "polygon": [lat, lon, lat, lon, ...], "areaKm2": 0.53,
            "bounds": { "minX": -395.1, "minZ": -333.6, "maxX": 395.1, "maxZ": 333.6 } },
  "sources": [ { "name": "osm", "count": 993, "attribution": "..." } ],
  "warnings": [],
  "conflicts": { "fixed": true, "roadsChecked": true, "overlapCut": 41, "overlapDropped": 2,   // optional, see
                 "roadMoved": 9, "roadCut": 1, "roadDropped": 3, "osmOnRoad": 0,              // "Overlaps and roads"
                 "dropped": [ { "id": "ms/48.97,37.81", "reason": "road", "against": "osm/way/123" } ] },
  "elevation": { "sources": ["Copernicus GLO-30"], "originGround": 140.0,   // optional block
                 "minGround": 128.0, "maxGround": 171.0, "attribution": ["..."] },
  "buildings": [
    {
      "id": "osm/way/106271616",          // or "ms/<lat>,<lon>"
      "source": "osm",                    // "osm" | "microsoft"
      "category": "house",                // house, apartments, industrial, commercial, public, religious,
                                          // garage, shed, farm, utility, ruins, construction, other
      "categorySource": "tag",            // "tag" | "guessed"
      "buildingTag": "house",             // raw OSM building=* value
      "name": "",
      "lat": 49.8341, "lon": 36.6861,     // centre of the oriented box
      "x": 79.3, "z": 11.2,               // same point in metres east / north of origin
      "width": 8.2, "length": 11.5,       // oriented box, width <= length (m)
      "rotation": 37.5,                   // long side bearing, degrees clockwise from north = Unity Y rotation
      "area": 82.1,                       // real footprint area (m²)
      "height": 5.0, "levels": 1,
      "heightSource": "estimated",        // "tag" | "levels" | "microsoft" | "estimated"
      "groundElevation": 142.0,           // terrain at the box centre, m above sea level (optional)
      "y": 2.0,                           // groundElevation minus the origin's ground = Unity Y of the box base (optional)
      "footprint": [dx, dz, dx, dz, ...], // outer ring, metres relative to (x, z), east/north axes, CCW
      "footprintAdjusted": [              // optional: how "Overlaps and roads" changed the outline
        { "action": "moved", "reason": "road", "against": "osm/way/123", "movedM": 1.6 },
        { "action": "cut", "reason": "overlap", "against": "osm/way/456", "areaBefore": 92.0 } ],
      "roof": {                           // optional, see "Roofs"; absent = nothing known
        "shape": "gabled", "shapeSource": "estimated", "shapeConfidence": 0.55,
        "renderAs": "gabled",             // nearest of flat / gabled / hipped
        "ridge": "along", "ridgeBearing": 37.5,   // ridge direction, degrees clockwise from north (0..180)
        "pitch": 35, "rise": 2.9,         // slope in degrees; roof height above the eaves in metres
        "material": "metal", "materialSource": "estimated",
        "color": "#6e4636", "colorSource": "estimated", "colorConfidence": 0.25
      },
      "condition": {                      // optional, see "Building condition"; absent = unknown
        "state": "destroyed",             // "intact" | "damaged" | "heavily_damaged" | "destroyed"
        "basis": "reported",              // "observed" | "reported" | "mapped" | "estimated"
        "confidence": "medium", "asOf": "2026-09-23",   // date the state applies to
        "sources": ["src-slavinfo-2026-09-24"], "note": "School No. 3 reported destroyed ..."
      }
    }
  ]
}
```

### Coordinates

`x` / `z` are metres on a local east-north tangent plane at `origin`, on the WGS84
ellipsoid: `+x` = east, `+z` = north, which maps directly to Unity X / Z. Distortion
stays below a few centimetres within ~30 km of the origin. To place a building
the way Unity expects: put a box of `width × height × length` at `(x, y, z)`
with Y rotation `rotation`. Its local Z axis then runs along the long side. `y` is the
ground under the box, so the box centre sits at `y + height / 2`.

To make several exports line up in one world, give them all the same **fixed
origin**. Every building also carries `lat` / `lon`, so it can be re-projected
to any other origin later.

### Ground elevation of buildings

Each building gets `groundElevation` (metres above sea level) and `y` (relative to the
ground at the origin, so the origin is Y = 0). It is sampled once, at the box centre, from
the same Copernicus model as the heightmap, so buildings sit on the terrain you build
from the heightmap. If that model cannot be loaded, it falls back to SRTM 30 m via
OpenTopoData, then Copernicus GLO-90 via Open-Meteo (cached in `cache/elevation.json`).
The model is accurate to a few metres and cannot see slopes smaller than 30 m, so a
raycast onto your terrain is still the safest final snap. Buildings with no data have
neither field. Turn it off with the checkbox or `--no-elevation`.

### Roofs

Each building can carry a `roof` block for generators. Every value says where it came from
(`shapeSource`, `colorSource`, `materialSource`):

| Source | Meaning |
|---|---|
| `reviewed` | A person checked it against an overhead image or photo in this app (`visualDetails`). Wins over `mapped`. |
| `mapped` | An OpenStreetMap `roof:*` tag on the building (shape, colour, material, angle, height, direction, orientation). |
| `estimated` | **Nobody observed this roof.** It is the typical roof for this kind of building in Ukraine: houses gabled (or hipped when nearly square and small) with sheet metal, asbestos cement or tiles in greys, browns, reds and greens; apartment and commercial blocks flat with bitumen; sheds and garages single-slope or flat. Kinds with no useful prior (religious, ruins, construction, unclassified) stay unknown. |

OSM maps roof details for almost no buildings (3 of 20,971 in Lyman), so without estimates a
generator would have nothing to go on. Estimates are low confidence (`shapeConfidence` 0.35–0.75,
`colorConfidence` 0.25–0.45, lower for guessed categories and complex footprints) and
deterministic: the same building id always gets the same colour, so a re-extraction does not
repaint the town while neighbours still differ.

| Field | Meaning |
|---|---|
| `shape` | `flat`, `gabled`, `hipped`, `skillion`, or any other OSM `roof:shape` kept as mapped (`onion`, `mansard`, ...). |
| `renderAs` | The nearest of `flat` / `gabled` / `hipped`, for generators that only build those. A skillion renders as `flat`. Absent when the shape is unrecognised. |
| `ridge`, `ridgeBearing` | Whether the ridge runs `along` or `across` the long side, and its bearing in degrees clockwise from north (0..180). Assumed along the long side unless `ridgeSource` is `mapped`. |
| `slopeBearing` | Direction a skillion slopes down, from `roof:direction`. Mapped only. |
| `pitch`, `rise` | Slope in degrees, and roof height above the eaves in metres (walls are `height - rise`; it is part of the total `height`). Derived from the shape unless `pitchSource` / `riseSource` say `mapped`. |
| `material`, `color` | Roof material (`metal`, `clay_tiles`, `bitumen`, `asbestos_cement`, `slate`, ...) and `#rrggbb`. `colorRaw` keeps a mapped colour that could not be parsed. |

To show only facts, ignore any value whose `...Source` is `estimated`, or switch the estimates off:
untick **Fill in typical roofs where nothing is mapped** or pass `--no-roof-estimates`, and only
mapped and reviewed values are exported. `roofInfo` in the export says whether estimates were
added and counts roofs by source. In the map, **Colour by → Roof shape / Roof colour** shows the
result; estimated roofs have a dashed outline. Roof colours are never read from basemap tiles, and
Sentinel-2 is too coarse; for real colours use **Import overhead image**.

The companion Unity generator (`Assets/GeoWorld`) reads the block itself: flat, gabled, hipped and
skillion (single-slope) roofs, the ridge direction, the rise and the colour, with a generation setting
to leave estimated values out. A generator of your own that only builds flat, gabled and hipped roofs
can use `renderAs`, `ridgeBearing`, `rise` and `color`.

An export made before roofs existed has no `roof` blocks. Extract the area again, or add them to the
file you already have (nothing is downloaded; add `--no-estimates` for only mapped and reviewed data):

```bash
py -3 tools/add_roofs.py exports/lyman_city.json
```

## Terrain & ground package (`geo-environment`, version 1)

Tick *Terrain heightmap* and *Ground map* and the download also holds this package.
Positions are in the same local metres as the buildings, so everything lines up.

| File | What it is |
|---|---|
| `buildings.json` | The buildings, unchanged |
| `environment.json` | Terrain description, material list, roads, railways, waterways, water, land areas, trees |
| `heightmap.r16` | Ground heights: 16-bit unsigned little-endian, no header, rows north to south |
| `materials.png` | Ground material at every spot: 8-bit greyscale, the pixel value is the material id |
| `canopy.png` | 255 where trees stand, 0 elsewhere, on the same grid as `materials.png` |
| `materials_preview.png`, `hillshade.png` | Pictures of the two maps, to look at |

### Heightmap

Sample `(i, j)` sits at `x = minX + i × cellSize` and `z = maxZ − j × cellSize`, so the first row
is the northern edge. Height in metres above sea level is
`minHeight + value / 65535 × (maxHeight − minHeight)`, and subtracting `originGround` gives world Y,
the same Y as the buildings' `y`. The step is 4 m by default; a selection that would need more than
2049 samples a side is coarsened automatically. Cell edges are snapped to multiples of the step
counted from the origin, so exports that share an origin and a step line up sample for sample.

- **Unity**: Terrain Settings → Import Raw: depth 16 bit, byte order Windows, the `width` × `height` from
  `environment.json`. Terrain height is `maxHeight − minHeight`; put the terrain at
  `Y = minHeight − originGround`. Unity wants square power-of-two-plus-one sizes, so crop or pad, or cut a big
  area into several terrains. If the result is mirrored, tick *Flip Vertically*.
- **Godot** (Terrain3D, HTerrain): both import `.r16`. Give them the same height range.

### Ground materials

`materials.png` holds one material id per pixel (1 m by default). Pixel `(i, j)` covers `x` from
`minX + i × cellSize` and `z` from `maxZ − j × cellSize` downwards. The ids are listed in
`environment.json` under `materials.classes`:

`grass`, `cropland`, `forest`, `scrub`, `bare`, `sand`, `rock`, `wetland`, `water`, `urban`, `asphalt`, `paving`,
`gravel`, `dirt_road`, `path`, `rail`, `building`, `snow`, `orchard`.

How a pixel gets its material, from bottom to top:

1. ESA WorldCover gives the land cover (10 m). Tree cover that stands among buildings counts as `grass`,
   because it is garden and street trees, not forest floor.
2. OpenStreetMap areas replace it where people mapped them: farmland, meadows, forest, scrub, wetland, water,
   parks, sand, car parks and so on. Zoning (residential, industrial, ...) is exported but not painted.
3. Waterways, railways and roads are drawn on top at their width. Bridges and tunnels are left out.
4. Building footprints are `building`.

The tables that decide this are in `extractor/materials.py`; edit them for your game. For soft edges in
the engine, blur the weights of each material when you turn the map into terrain layers.

### Roads, water, fields, trees (`environment.json`)

```jsonc
{
  "format": "geo-environment", "version": 1,
  "origin": { "lat": 49.834, "lon": 36.6865 },
  "bounds": { "minX": -1332, "minZ": -1335, "maxX": 1332, "maxZ": 1335 },
  "terrain":   { "source": "...", "heightmap": { "file": "heightmap.r16", "width": 667, "height": 669, "cellSize": 4.0,
                 "minX": -1332, "maxZ": 1336, "minHeight": 84.8, "maxHeight": 156.1, "originGround": 138.4, ... } },
  "materials": { "file": "materials.png", "canopyFile": "canopy.png", "width": 2664, "height": 2670, "cellSize": 1.0,
                 "minX": -1332, "maxZ": 1335, "classes": [ { "id": 1, "name": "grass", "color": "#8fc05a", ... } ],
                 "areaKm2": { "grass": 2.0, ... } },
  "roads": [ { "id": "osm/way/1", "type": "residential", "kind": "road",     // "road" | "path"
               "material": "asphalt", "width": 5.5, "widthSource": "default", // "tag" | "lanes" | "default"
               "surface": "", "lanes": 0, "oneway": false, "bridge": false, "tunnel": false, "layer": 0,
               "name": "", "ref": "Н-26", "nationalClass": "H",           // Ukrainian road number and its class M H P T O C
               "surfaceKind": "asphalt", "surfaceGuess": true,             // finer than "material", see below
               "smoothness": "", "tracktype": "", "wear": 0.55, "wearSource": "class",   // 0 like new .. 1 ruined
               "points": [x, z, x, z, ...] } ],
  "railways":  [ { "id": "...", "type": "rail", "width": 3.6, "bridge": false, "tunnel": false, "points": [...] } ],
  "waterways": [ { "id": "...", "type": "stream", "width": 2.0, "widthSource": "default", "points": [...] } ],
  "water":     [ { "id": "...", "tag": "natural=water", "type": "lake", "area": 11258.2, "rings": [[x, z, ...], [hole...]] } ],
  "areas":     [ { "id": "...", "tag": "landuse=farmland", "material": "cropland", "crop": "", "leafType": "",
                   "area": 80000.0, "rotation": 37.5,     // bearing of the long side: the way crop rows usually run
                   "rings": [[x, z, ...], [hole...]] } ],   // land cover and zoning ("material": "" = zoning)
  "trees":     [ { "id": "osm/node/1", "x": 10.0, "z": 5.0, "y": 2.0, "genus": "", "species": "", "leafType": "",
                   "leafCycle": "", "height": 0, "crown": 0 } ],            // trees someone mapped one by one
  "treeRows":  [ { "id": "...", "points": [...] } ],  "hedges": [ { "id": "...", "points": [...] } ],
  "warnings": [], "summary": { ... }
}
```

Lines are lists of `x, z` pairs in metres, simplified to 25 cm. `rings` are flat `x, z` lists: the first is the
outline, the rest are holes. Roads are cut at the edge of the selection. Widths are estimated: from the `width` tag
if the road has one, otherwise from `lanes` × 3.2 m, otherwise from the road class (`extractor/materials.py`).
`y` of a tree is its height above the origin's ground.

Road surface and condition (extra fields on roads; absent in older packages, which engines must treat as unknown):
`surfaceKind` is `asphalt`, `concrete`, `concrete_plates`, `paving_stones`, `cobblestone`, `gravel` or `dirt`, from the
`surface` tag, otherwise from the ground material (`surfaceGuess` is true then). A service or unclassified road with no
surface tag outside built-up areas (fewer than six buildings within 50 m of its middle) is `gravel`, not asphalt. `wear` is an
estimate of how worn the road is: from `smoothness` (`wearSource` `smoothness`), else `tracktype`, else a typical value for
the road type, on the worn side because most Ukrainian roads have not been resurfaced in decades, kept up a little better on
state roads. `ref` is the OpenStreetMap road number, `nationalClass` the highest Ukrainian class in it: `M` international,
`H` national, `P` regional, `T` territorial, `O` and `C` local (Cyrillic and Latin letters both read; European E numbers
ignored). `smoothness` and `tracktype` are passed on as tagged.

**Trees.** OSM rarely has single trees, so a forest is described by the `canopy.png` mask (where trees stand)
plus the `forest` areas with their `leafType`. Scatter your tree prefabs where the mask is 255, at the density you like,
and use `trees`, `treeRows` and `hedges` for the ones that were mapped. Roads, water and buildings are already cut out
of the mask.

### Street objects, fences and prop placement

Terrain & ground exports also contain optional `props` and `barriers` arrays. Turn on
**Street objects & fences** in the map preview to inspect mapped details. Re-extract older
areas to collect these layers; older packages still import normally.

- `props`: OSM point locations for lamps, benches, bins, bus stops, shelters, bicycle racks,
  post boxes, bollards, gates, power poles, traffic signals and entrances. Entrances are
  access markers for keeping decoration clear, rather than separate door models.
- `barriers`: fence, wall, retaining wall and guard-rail lines, clipped to the export bounds.
- Each object includes its source ID, type, `placementSource: "mapped"`, position or line,
  and available name/material/colour/dimensions. Missing dimensions are zero (unknown).
  Point bearings are exported only when explicitly numeric or compass directions;
  `rotationSource` is `tag` or `unknown`. Indoor/underground point objects are excluded.
- Road `lit` and `sidewalk` tags are retained when present. Empty values mean unknown.

The companion Unity importer supports a **Prop catalog** asset for assigning prefabs by
type. Without one it uses shared preview models. It builds hedges/fences along their lines,
leaves openings around mapped gates, and uses `canopy.png` and tree rows for repeatable
vegetation placement. Generated vegetation avoids roads, paths, railways, water, building
outlines, entrances and existing objects. **Generated street decoration** is optional:
it adds plausible lamps near built-up roads and benches along park paths, labeled generated.
Explicitly unlit roads do not receive generated lamps. These are decorative estimates,
not observations of real objects. Coverage of mapped street details varies by area.

### Other limits to know about

- The elevation model is a surface model (it includes treetops and roofs) at 30 m. Forests read a few metres high,
  and nothing smaller than 30 m shows. The heightmap is smooth by design.
- WorldCover is from 2021 and has 10 m pixels, so its edges are blocky. OSM shapes are crisp but only exist where mapped.
- OSM completeness varies. Check the preview before relying on a village.
- Fields are `cropland` areas. What grows on them is only known where someone mapped a `crop` tag.

## Tests

Offline unit tests for the terrain and ground code (painting, PNG writing, the TIFF predictor, OSM parsing, the
material tables):

```bash
py -3 -m unittest discover -s tests
```

## Credits

Data © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright) (ODbL) ·
[Microsoft Building Footprints](https://github.com/microsoft/GlobalMLBuildingFootprints) (ODbL) ·
[ESA WorldCover 2021](https://esa-worldcover.org) (CC BY 4.0) ·
Copernicus DEM GLO-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018, provided under COPERNICUS by the European Union and ESA ·
Imagery © Esri.
