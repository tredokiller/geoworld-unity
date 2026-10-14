"""The ground around the buildings: terrain, ground materials, roads, water, fields and trees.

Produces a package of files (heightmap, material map, canopy mask, environment.json)
that a Unity or Godot importer can turn into a landscape.
"""
import datetime as dt
import math

from . import classify, geo, landcover, materials, osmenv, props, raster, roadinfo, terrain
from .common import Cancelled

FORMAT = "geo-environment"
VERSION = 1
GENERATOR = "BuildingExtractor 1.0"
MAX_PIXELS = 16_000_000
ROAD_TOLERANCE = 0.25   # m, line simplification
AREA_TOLERANCE = 0.5
PATH_TYPES = roadinfo.PATH_TYPES
# Where a tree cannot stand.
NO_TREES = ("water", "asphalt", "paving", "gravel", "dirt_road", "path", "rail", "building")

ID = materials.ID


def _r(v, digits=2):
    return round(v, digits)


def _flat(points):
    return [_r(v) for p in points for v in p]


def _flag(tags, key):
    return tags.get(key, "no") not in ("no", "", None)


def _stage(progress, lo, hi):
    """Maps a 0..1 sub-progress into the lo..hi slice of the whole run."""
    return lambda frac, msg=None: progress(None if frac is None else lo + (hi - lo) * frac, msg)


class Result:
    def __init__(self):
        self.json = {}
        self.files = {}
        self.summary = {}
        self.warnings = []


def run(buildings, ring, proj, origin, dem, options, cache_dir, log, progress, is_cancelled):
    """Builds the environment package for the selection `ring` ([(lat, lon), ...]).

    buildings: processed buildings (for footprints); proj: the export's projection; dem: a
    terrain.Dem or None. options: heightmap (bool), ground (bool), heightCell, groundCell (m).
    """
    res = Result()
    want_height = bool(options.get("heightmap", True)) and dem is not None
    want_ground = bool(options.get("ground", True))

    sel = [proj.forward(la, lo) for la, lo in ring]
    bounds = (min(p[0] for p in sel), min(p[1] for p in sel), max(p[0] for p in sel), max(p[1] for p in sel))
    latlon_box = _latlon_box(proj, bounds)
    south, west, north, east = latlon_box

    env = {
        "format": FORMAT, "version": VERSION, "generator": GENERATOR,
        "createdUtc": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "projection": "local-enu-wgs84",
        "origin": {"lat": round(origin[0], 8), "lon": round(origin[1], 8)},
        "bounds": {"minX": _r(bounds[0]), "minZ": _r(bounds[1]), "maxX": _r(bounds[2]), "maxZ": _r(bounds[3])},
        "buildings": "buildings.json",
        "sources": [],
    }
    res.json = env

    # ---- terrain -----------------------------------------------------------
    origin_ground = None
    if dem is not None:
        origin_ground = dem.sampler()(origin[0], origin[1])
    hm = None
    if want_height:
        progress(0.0, "Terrain: heightmap")
        hm = terrain.build_heightmap(dem, proj, bounds, float(options.get("heightCell", 4.0)),
                                     _stage(progress, 0.0, 0.10), is_cancelled)
        log(f"Terrain: heightmap {hm.width} x {hm.height} samples, {hm.cell:g} m apart, "
            f"{hm.min_h:.1f} to {hm.max_h:.1f} m above sea level")
        hw, hh, rgba = terrain.hillshade(hm, 768)
        res.files["heightmap.r16"] = hm.to_r16()
        res.files["hillshade.png"] = raster.png_rgba(hw, hh, rgba)
        env["terrain"] = {
            "source": terrain.NAME, "attribution": terrain.ATTRIBUTION,
            "note": "A surface model: it reads a few metres high over forests and dense buildings.",
            "heightmap": {
                "file": "heightmap.r16", "encoding": "uint16-le",
                "width": hm.width, "height": hm.height, "cellSize": hm.cell,
                "minX": _r(hm.min_x), "maxZ": _r(hm.max_z), "rowOrder": "north-to-south",
                "minHeight": _r(hm.min_h), "maxHeight": _r(hm.max_h),
                "originGround": _r(origin_ground) if origin_ground is not None else None,
            },
            "previewFile": "hillshade.png",
            "previewBounds": _corner_bounds(
                proj, hm.min_x, hm.max_z - (hm.height - 1) * hm.cell, hm.min_x + (hm.width - 1) * hm.cell, hm.max_z),
        }
        env["sources"].append({"name": "terrain", "attribution": terrain.ATTRIBUTION})

    if not want_ground:
        _finish(res, env, hm, None, None)
        progress(1.0, "Environment: done")
        return res

    # ---- map data ------------------------------------------------------------
    features = None
    pad = 0.0006
    fetch_ring = [(south - pad, west - pad), (south - pad, east + pad), (north + pad, east + pad), (north + pad, west - pad)]
    try:
        progress(0.10, "OSM: roads, water, land cover, trees")
        features = osmenv.fetch(fetch_ring, log, _stage(progress, 0.10, 0.60), is_cancelled)
        env["sources"].append({"name": "osm", "attribution": "(c) OpenStreetMap contributors, ODbL 1.0"})
    except Cancelled:
        raise
    except Exception as e:
        res.warnings.append("OpenStreetMap roads, water and land cover could not be loaded, so the ground "
                            "map has satellite land cover only. Try again later.")
        log(f"WARNING: OSM terrain features failed: {e}")

    # ---- grid ----------------------------------------------------------------
    cell = float(options.get("groundCell", 1.0))
    while True:
        min_x = math.floor(bounds[0] / cell) * cell
        max_z = math.ceil(bounds[3] / cell) * cell
        width = int(math.ceil((bounds[2] - min_x) / cell))
        height = int(math.ceil((max_z - bounds[1]) / cell))
        if width * height <= MAX_PIXELS:
            break
        cell *= 2
    log(f"Ground map: {width} x {height} px at {cell:g} m")

    canvas = raster.Canvas(width, height, min_x, max_z, cell, fill=ID["grass"])
    canopy = None

    # ---- satellite land cover as the base layer ---------------------------------
    try:
        progress(0.60, "Land cover")
        wc = landcover.WorldCover(south - 0.002, west - 0.002, north + 0.002, east + 0.002, cache_dir, log,
                                  _stage(progress, 0.60, 0.66), is_cancelled)
        lut = bytearray(256)
        for code, name in materials.WORLDCOVER.items():
            lut[code] = ID[name]
        canvas.data[:] = wc.render(proj, min_x, max_z, cell, width, height, bytes(lut))
        tree_lut = bytearray(256)
        tree_lut[landcover.TREES] = 255
        canopy = wc.render(proj, min_x, max_z, cell, width, height, bytes(tree_lut), ground=False)
        env["sources"].append({"name": "worldcover", "attribution": landcover.ATTRIBUTION})
    except Cancelled:
        raise
    except Exception as e:
        res.warnings.append("Satellite land cover could not be loaded, so ground outside mapped areas is plain grass. "
                            "Try again later.")
        log(f"WARNING: land cover failed: {e}")
    if canopy is None:
        canopy = bytearray(width * height)

    # ---- vectors ---------------------------------------------------------------
    progress(0.70, "Projecting map features")
    rect = (bounds[0] - 5, bounds[1] - 5, bounds[2] + 5, bounds[3] + 5)
    layers = _vectors(features, proj, rect, log, buildings) if features else _empty_layers()
    env.update({k: layers[k] for k in LAYER_KEYS})

    # ---- paint -------------------------------------------------------------------
    progress(0.76, "Painting ground materials")
    _paint(canvas, canopy, layers, buildings, is_cancelled)

    # Trees stand where the ground allows it.
    allowed = bytearray([255]) * 256
    for i, name, _, _ in materials.MATERIALS:
        if name in NO_TREES:
            allowed[i] = 0
    mask = bytes(canvas.data).translate(bytes(allowed))
    canopy = (int.from_bytes(canopy, "little") & int.from_bytes(mask, "little")).to_bytes(width * height, "little")

    # Trees from OSM get their ground height from the heightmap.
    if hm is not None and origin_ground is not None:
        for t in layers["trees"]:
            t["y"] = _r(hm.at(t["x"], t["z"]) - origin_ground, 2)

    progress(0.90, "Writing images")
    res.files["materials.png"] = raster.png_gray8(width, height, canvas.data)
    palette = materials.palette()
    alpha = [0] + [255] * (len(palette) - 1)
    res.files["materials_preview.png"] = raster.png_indexed(width, height, canvas.data, palette, alpha)
    res.files["canopy.png"] = raster.png_gray8(width, height, canopy)

    counts = {materials.MATERIALS[i][1]: canvas.data.count(i) for i in range(1, len(materials.MATERIALS))}
    area_km2 = {k: round(v * cell * cell / 1e6, 4) for k, v in counts.items() if v}
    env["materials"] = {
        "file": "materials.png", "canopyFile": "canopy.png", "previewFile": "materials_preview.png",
        "width": width, "height": height, "cellSize": cell, "minX": _r(min_x), "maxZ": _r(max_z),
        "rowOrder": "north-to-south",
        "note": "Pixel value = material id. Pixel (i, j) covers x from minX + i * cellSize and z from maxZ - j * cellSize, "
                "looking down. canopy.png is 255 where trees stand.",
        "classes": materials.legend(),
        "areaKm2": area_km2,
        "previewBounds": _corner_bounds(proj, min_x, max_z - height * cell, min_x + width * cell, max_z),
    }
    _finish(res, env, hm, (width, height, cell), area_km2)
    res.summary["canopyShare"] = round(canopy.count(255) / max(1, width * height), 4)
    progress(1.0, "Environment: done")
    return res


LAYER_KEYS = ("roads", "railways", "waterways", "water", "areas", "trees", "treeRows", "hedges", "props", "barriers")


def _empty_layers():
    return {k: [] for k in LAYER_KEYS}


def _latlon_box(proj, bounds):
    pts = [proj.inverse(x, z) for x in (bounds[0], bounds[2]) for z in (bounds[1], bounds[3])]
    return min(p[0] for p in pts), min(p[1] for p in pts), max(p[0] for p in pts), max(p[1] for p in pts)


def _corner_bounds(proj, x0, z0, x1, z1):
    """[[south, west], [north, east]] of a local rectangle, for placing an image on the map."""
    s, w, n, e = _latlon_box(proj, (x0, z0, x1, z1))
    return [[round(s, 7), round(w, 7)], [round(n, 7), round(e, 7)]]


# ---------------------------------------------------------------- vectors

def _vectors(features, proj, rect, log, buildings=()):
    out = _empty_layers()
    box = rect
    settlement = roadinfo.Settlement((b["x"], b["z"]) for b in buildings)

    def project(pts):
        return [proj.forward(la, lo) for la, lo in pts]

    # Roads and paths
    for item in features["roads"]:
        tags = item["tags"]
        highway = tags["highway"]
        rank, default_w, default_mat = materials.ROADS[highway]
        width, width_source = roadinfo.road_width(tags, highway, default_w)
        line = project(item["line"])
        mid = line[len(line) // 2]
        settled = settlement.is_settled(mid[0], mid[1]) if buildings else True
        material = _road_material(tags, highway, default_mat, settled)
        kind, kind_guessed = roadinfo.surface_kind(tags, material)
        national = roadinfo.national_class(tags.get("ref", ""))
        road_wear, wear_source = roadinfo.wear(tags, highway, national)
        bridge, tunnel = _flag(tags, "bridge"), _flag(tags, "tunnel") or _flag(tags, "culvert")
        for k, part in enumerate(geo.clip_polyline(line, box)):
            part = geo.simplify(part, ROAD_TOLERANCE)
            if len(part) < 2:
                continue
            out["roads"].append({
                "id": item["id"] + (f"#{k + 1}" if k else ""), "type": highway,
                "kind": "road" if roadinfo.is_road(highway) else "path",
                "material": material, "width": _r(width, 1), "widthSource": width_source,
                "surface": tags.get("surface", ""), "lanes": int(classify.parse_number(tags.get("lanes")) or 0),
                "lit": tags.get("lit", ""), "sidewalk": tags.get("sidewalk", ""),
                "oneway": tags.get("oneway") in ("yes", "true", "1", "-1"), "bridge": bridge, "tunnel": tunnel,
                "layer": int(classify.parse_number(tags.get("layer")) or 0), "name": tags.get("name", ""),
                "ref": tags.get("ref", ""), "nationalClass": national,
                "surfaceKind": kind, "surfaceGuess": kind_guessed,
                "smoothness": tags.get("smoothness", ""), "tracktype": tags.get("tracktype", ""),
                "wear": _r(road_wear, 2), "wearSource": wear_source,
                "points": _flat(part), "_pts": part, "_rank": rank,
            })

    for item in features["railways"]:
        tags = item["tags"]
        width = materials.RAILWAYS[tags["railway"]]
        for k, part in enumerate(geo.clip_polyline(project(item["line"]), box)):
            part = geo.simplify(part, ROAD_TOLERANCE)
            if len(part) < 2:
                continue
            out["railways"].append({
                "id": item["id"] + (f"#{k + 1}" if k else ""), "type": tags["railway"], "width": width,
                "bridge": _flag(tags, "bridge"), "tunnel": _flag(tags, "tunnel"),
                "electrified": tags.get("electrified", "no") not in ("no", ""), "name": tags.get("name", ""),
                "points": _flat(part), "_pts": part,
            })

    for item in features["waterways"]:
        tags = item["tags"]
        kind = tags["waterway"]
        w = classify.parse_number(tags.get("width"))
        width, source = (w, "tag") if w and 0.2 <= w <= 500 else (materials.WATERWAYS[kind], "default")
        for k, part in enumerate(geo.clip_polyline(project(item["line"]), box)):
            part = geo.simplify(part, ROAD_TOLERANCE)
            if len(part) < 2:
                continue
            out["waterways"].append({
                "id": item["id"] + (f"#{k + 1}" if k else ""), "type": kind, "width": _r(width, 1), "widthSource": source,
                "tunnel": _flag(tags, "tunnel") or _flag(tags, "culvert"),
                "intermittent": tags.get("intermittent") == "yes", "name": tags.get("name", ""),
                "points": _flat(part), "_pts": part,
            })

    # Land cover polygons, water bodies and zoning
    for a in features["areas"]:
        rings = [geo.clean_ring(project(r)) for r in a["rings"]]
        rings = [r for r in rings if len(r) >= 3]
        if not rings:
            continue
        outer_box = geo.bbox(rings[0])
        if not geo.boxes_overlap(outer_box, box):
            continue
        rings = [geo.simplify_ring(r, AREA_TOLERANCE) for r in rings]
        area_m2 = abs(geo.signed_area(rings[0])) - sum(abs(geo.signed_area(r)) for r in rings[1:])
        tags = a["tags"]
        # Bearing of the long side, clockwise from north: the way crop rows usually run.
        bearing = geo.min_area_rect(rings[0])[4] if len(rings[0]) >= 3 else 0.0
        entry = {
            "id": a["id"], "tag": a["tag"], "material": a["material"] or "", "name": tags.get("name", ""),
            "area": _r(max(area_m2, 0.0), 1), "rotation": _r(bearing, 1),
            "rings": [_flat(r) for r in rings], "_rings": rings, "_priority": a["priority"],
        }
        if a["material"] == "water":
            entry["type"] = tags.get("water") or tags.get("natural") or tags.get("landuse") or tags.get("waterway") or "water"
            out["water"].append(entry)
        else:
            entry["leafType"] = tags.get("leaf_type", "")
            entry["crop"] = tags.get("crop", "")
            entry["surface"] = tags.get("surface", "")
            out["areas"].append(entry)

    for t in features["trees"]:
        x, z = proj.forward(*t["pos"])
        if not (box[0] <= x <= box[2] and box[1] <= z <= box[3]):
            continue
        tags = t["tags"]
        out["trees"].append({
            "id": t["id"], "x": _r(x), "z": _r(z),
            "genus": tags.get("genus", ""), "species": tags.get("species", ""),
            "leafType": tags.get("leaf_type", ""), "leafCycle": tags.get("leaf_cycle", ""),
            "height": _r(classify.parse_number(tags.get("height")) or 0.0, 1),
            "crown": _r(classify.parse_number(tags.get("diameter_crown")) or 0.0, 1),
        })
    for key, src in (("treeRows", "tree_rows"), ("hedges", "hedges")):
        for item in features[src]:
            tags = item["tags"]
            for k, part in enumerate(geo.clip_polyline(project(item["line"]), box)):
                part = geo.simplify(part, ROAD_TOLERANCE)
                if len(part) < 2:
                    continue
                out[key].append({
                    "id": item["id"] + (f"#{k + 1}" if k else ""), "genus": tags.get("genus", ""),
                    "species": tags.get("species", ""), "leafType": tags.get("leaf_type", ""),
                    "height": _r(classify.parse_number(tags.get("height")) or 0.0, 1), "points": _flat(part),
                })
    out["props"], out["barriers"] = props.project(features, proj, box)
    log(f"Features in the area: {len(out['roads'])} roads and paths, {len(out['railways'])} railways, "
        f"{len(out['waterways'])} waterways, {len(out['water'])} water bodies, {len(out['areas'])} land areas, "
        f"{len(out['trees'])} mapped trees, {len(out['props'])} street objects, {len(out['barriers'])} barriers")
    return out


_road_width = roadinfo.road_width  # the buildings' road check uses the same widths


def _road_material(tags, highway, default, settled=True):
    """Ground-map material of a road: its surface tag, else its track grade, else a guess from its class.

    The guess is asphalt, except that an untagged service or unclassified road with no buildings around
    it (a farm or field road) is taken to be gravel: yard drives in a town are paved, those outside
    mostly are not. `settled` says whether there are buildings around.
    """
    surface = materials.SURFACE.get((tags.get("surface") or "").strip().lower())
    if surface:
        if highway in PATH_TYPES and surface == "dirt_road" and highway != "track":
            return "path"
        return surface
    if highway == "track":
        return materials.TRACK_GRADE.get(tags.get("tracktype"), default)
    if not settled and highway in ("service", "unclassified"):
        return "gravel"
    return default


# ---------------------------------------------------------------- painting

def _paint(canvas, canopy, layers, buildings, is_cancelled):
    # Land cover and water polygons, low priority first, bigger before smaller.
    shapes = layers["areas"] + layers["water"]
    shapes = [s for s in shapes if s["material"]]
    shapes.sort(key=lambda s: (s["_priority"], -s["area"]))
    for n, s in enumerate(shapes):
        if n % 200 == 0 and is_cancelled():
            raise Cancelled()
        canvas.fill_polygon(s["_rings"], ID[s["material"]])
        # Mapped woods and orchards also carry trees.
        if s["material"] in ("forest", "orchard"):
            _paint_mask(canvas, canopy, s["_rings"])

    for w in layers["waterways"]:
        if not w["tunnel"]:
            canvas.stroke(w["_pts"], w["width"], ID["water"])
    for r in layers["railways"]:
        if not (r["tunnel"] or r["bridge"]):
            canvas.stroke(r["_pts"], r["width"], ID["rail"])
    roads = [r for r in layers["roads"] if not (r["tunnel"] or r["bridge"])]
    roads.sort(key=lambda r: r["_rank"])
    for n, r in enumerate(roads):
        if n % 500 == 0 and is_cancelled():
            raise Cancelled()
        canvas.stroke(r["_pts"], r["width"], ID[r["material"]])

    for b in buildings:
        fp = b.get("footprint") or []
        ring = [(b["x"] + fp[i], b["z"] + fp[i + 1]) for i in range(0, len(fp) - 1, 2)]
        if len(ring) >= 3:
            canvas.fill_polygon([ring], ID["building"])


def _paint_mask(canvas, mask, rings):
    """Sets 255 in a byte mask with the same grid as the canvas where the polygon covers."""
    scratch = raster.Canvas(canvas.width, canvas.height, canvas.min_x, canvas.max_z, canvas.cell)
    scratch.data = mask
    scratch.fill_polygon(rings, 255)


# ---------------------------------------------------------------- output

def _finish(res, env, hm, grid, area_km2):
    s = res.summary
    s["heightmap"] = None if hm is None else {
        "width": hm.width, "height": hm.height, "cell": hm.cell, "min": round(hm.min_h, 1), "max": round(hm.max_h, 1)}
    s["groundMap"] = None if grid is None else {"width": grid[0], "height": grid[1], "cell": grid[2]}
    s["materials"] = area_km2 or {}
    s["roads"] = {"count": len(env.get("roads", [])), "km": round(sum(
        _length(r["points"]) for r in env.get("roads", [])) / 1000.0, 2)}
    s["railways"] = len(env.get("railways", []))
    s["waterways"] = len(env.get("waterways", []))
    s["water"] = len(env.get("water", []))
    s["areas"] = len(env.get("areas", []))
    s["trees"] = len(env.get("trees", []))
    for key in ("props", "barriers", "treeRows", "hedges"):
        s[key] = len(env.get(key, []))
    s["files"] = {name: len(data) for name, data in res.files.items()}
    env["warnings"] = res.warnings
    env["summary"] = s
    # Private keys carry geometry used while painting; they do not belong in the file.
    for key in ("roads", "railways", "waterways", "water", "areas"):
        for item in env.get(key, []):
            for k in [k for k in item if k.startswith("_")]:
                del item[k]


def _length(flat):
    return sum(math.hypot(flat[i + 2] - flat[i], flat[i + 3] - flat[i + 1]) for i in range(0, len(flat) - 3, 2))


# ---------------------------------------------------------------- map preview

def preview_geojson(env):
    """Roads, water, land areas and trees of an environment.json as GeoJSON in lat/lon, for the web map.

    Uses a flat local approximation around the origin, which is off by about two metres at 5 km.
    """
    lat0, lon0 = env["origin"]["lat"], env["origin"]["lon"]
    m_lat, m_lon = geo.metres_per_degree(lat0)

    def ll(x, z):
        return [round(lon0 + x / m_lon, 6), round(lat0 + z / m_lat, 6)]

    def line(flat):
        return [ll(flat[i], flat[i + 1]) for i in range(0, len(flat) - 1, 2)]

    feats = []

    def add(layer, geom, **props):
        feats.append({"type": "Feature", "properties": {"layer": layer, **props}, "geometry": geom})

    for r in env.get("roads", []):
        add("road", {"type": "LineString", "coordinates": line(r["points"])}, type=r["type"], kind=r["kind"],
            width=r["width"], material=r["material"], name=r["name"], bridge=r["bridge"], tunnel=r["tunnel"],
            widthSource=r["widthSource"], surface=r["surface"], surfaceKind=r["surfaceKind"], wear=r["wear"],
            ref=r["ref"])
    for r in env.get("railways", []):
        add("rail", {"type": "LineString", "coordinates": line(r["points"])}, type=r["type"])
    for w in env.get("waterways", []):
        add("waterway", {"type": "LineString", "coordinates": line(w["points"])}, type=w["type"], width=w["width"], name=w["name"])
    for key, layer in (("water", "water"), ("areas", "area")):
        for a in env.get(key, []):
            if layer == "area" and not a["material"]:
                continue
            coords = []
            for ring in a["rings"]:
                pts = line(ring)
                coords.append(pts + pts[:1])
            add(layer, {"type": "Polygon", "coordinates": coords}, material=a["material"], tag=a["tag"], name=a["name"],
                area=a["area"])
    for t in env.get("trees", []):
        add("tree", {"type": "Point", "coordinates": ll(t["x"], t["z"])}, species=t["species"] or t["genus"])
    for key, layer in (("treeRows", "treeRow"), ("hedges", "hedge")):
        for h in env.get(key, []):
            add(layer, {"type": "LineString", "coordinates": line(h["points"])})
    for p in env.get("props", []):
        add("prop", {"type": "Point", "coordinates": ll(p["x"], p["z"])},
            type=p["type"], name=p["name"], source=p.get("placementSource", "mapped"),
            photoId=p.get("photoId"), confidence=p.get("confidence"), capturedAt=p.get("capturedAt"))
    for b in env.get("barriers", []):
        add("barrier", {"type": "LineString", "coordinates": line(b["points"])},
            type=b["type"], name=b["name"], source="mapped")
    return {"type": "FeatureCollection", "features": feats}
