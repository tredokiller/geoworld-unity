"""Turns raw footprints into the geo-buildings JSON consumed by Unity."""
import datetime as dt
import math

from . import appearance, classify, condition, conflicts, geo, microsoft, osm, roofs, terrain
from . import elevation as elevation_api
from .common import Cancelled

FORMAT = "geo-buildings"
VERSION = 1
GENERATOR = "BuildingExtractor 1.0"

ATTRIBUTION = {
    "osm": "(c) OpenStreetMap contributors, ODbL 1.0",
    "microsoft": "Microsoft Global ML Building Footprints, ODbL 1.0",
}


def _r(v, digits=2):
    return round(v, digits)


def run(polygon, sources, cache_dir, origin=None, min_area=6.0, with_elevation=True, dem_sampler=None,
        estimate_roofs=True, fix_conflicts=True, log=print, progress=lambda f, msg=None: None,
        is_cancelled=lambda: False):
    """Extracts buildings inside `polygon` ([(lat, lon), ...]).

    Returns (export_dict, preview_features). preview_features is a list of
    GeoJSON features with lat/lon outlines for showing on the web map.

    dem_sampler: optional f(lat, lon) -> ground height from terrain.Dem; without it, ground
    elevation comes from the web elevation services.

    estimate_roofs: fill in roof shape, pitch, material and colour that OSM does not map with typical
    values for the kind of building (marked "estimated"); off keeps only mapped and reviewed roof data.

    fix_conflicts: cut or drop outlines that overlap each other and move Microsoft outlines off OSM roads
    (see conflicts.py); off keeps every outline as its source has it.
    """
    ring = [(float(lat), float(lon)) for lat, lon in polygon]
    if len(ring) > 1 and ring[0] == ring[-1]:
        ring.pop()
    if len(ring) < 3:
        raise ValueError("Selection needs at least 3 points")
    if not sources:
        raise ValueError("Pick at least one data source")

    lats = [p[0] for p in ring]
    lons = [p[1] for p in ring]
    if origin is None:
        origin = ((min(lats) + max(lats)) / 2, (min(lons) + max(lons)) / 2)
    proj = geo.EnuProjection(origin[0], origin[1])
    sel = [proj.forward(lat, lon) for lat, lon in ring]
    sel_area_km2 = abs(geo.signed_area(sel)) / 1e6
    log(f"Selection: {sel_area_km2:.2f} km2, origin {origin[0]:.6f}, {origin[1]:.6f}")

    raw = {"osm": [], "microsoft": []}
    roads = None   # OSM highway lines for the road check; None when there are none to check against
    warnings = []
    steps = [s for s in ("osm", "microsoft") if s in sources]
    slots = len(steps) + (2 if with_elevation else 1)  # one per source, then processing, then elevation
    for i, src in enumerate(steps):
        base, span = i / slots, 1 / slots
        sub = lambda frac, msg=None, b=base, s=span: progress(None if frac is None else b + s * frac, msg)
        progress(base, f"Fetching {src}")
        try:
            if src == "osm":
                found = [] if fix_conflicts else None
                raw["osm"] = osm.fetch(ring, log, sub, is_cancelled, roads=found)
                roads = found
            else:
                raw["microsoft"] = microsoft.fetch(ring, cache_dir, log, sub, is_cancelled)
        except Cancelled:
            raise
        except Exception as e:
            if len(steps) == 1:
                raise
            # One source down shouldn't sink the whole preview; carry on with the other.
            name = "OpenStreetMap" if src == "osm" else "Microsoft ML footprints"
            warnings.append(f"{name} could not be loaded, so the result has no {name} buildings. Try again later.")
            log(f"WARNING: {name} failed: {e}")
    if fix_conflicts and roads is None and raw["microsoft"]:
        # Microsoft outlines without OSM buildings: the roads still come from OSM
        try:
            roads = osm.fetch_roads(ring, log, lambda f, m=None: None, is_cancelled)
        except Cancelled:
            raise
        except Exception as e:
            warnings.append("OpenStreetMap roads could not be loaded, so buildings on roads were not checked.")
            log(f"WARNING: OSM roads failed: {e}")

    progress(len(steps) / slots, "Processing footprints")
    processed = {"osm": [], "microsoft": []}
    seen_ids = set()
    skipped_small = skipped_outside = 0
    for src in steps:
        for n, b in enumerate(raw[src]):
            if n % 5000 == 0 and is_cancelled():
                raise Cancelled()
            if b["id"] in seen_ids:
                continue
            item = _process(b, proj, sel, min_area)
            if item == "small":
                skipped_small += 1
            elif item == "outside":
                skipped_outside += 1
            elif item:
                seen_ids.add(b["id"])
                processed[src].append(item)

    # OSM outlines are hand-mapped and carry tags, so they win over ML ones.
    duplicates = 0
    if processed["osm"] and processed["microsoft"]:
        index = geo.GridIndex(50.0)
        for item in processed["osm"]:
            index.insert(item, item["_box"])
        kept = []
        for n, ms in enumerate(processed["microsoft"]):
            if n % 2000 == 0 and is_cancelled():
                raise Cancelled()
            if _is_duplicate(ms, list(index.query_box(ms["_box"]))):
                duplicates += 1
            else:
                kept.append(ms)
        processed["microsoft"] = kept

    buildings = processed["osm"] + processed["microsoft"]
    buildings.sort(key=lambda b: (b["z"], b["x"]))
    conflict_info = None
    if fix_conflicts and buildings:
        progress(len(steps) / slots, "Checking overlaps and roads")
        lines = conflicts.road_lines(roads, proj) if roads is not None else None
        buildings, conflict_info = conflicts.resolve(buildings, lines, lambda item, pts: _reshape(item, pts, proj),
                                                     min_area, log)
    counts = {s: sum(1 for b in buildings if b["source"] == s) for s in steps}
    log(f"Result: {counts.get('osm', 0)} OSM + {counts.get('microsoft', 0)} Microsoft buildings "
        f"({duplicates} ML duplicates of OSM removed, {skipped_small} below {min_area} m2, "
        f"{skipped_outside} centred outside the selection)")

    elevation_info = None
    if with_elevation:
        base, span = (len(steps) + 0.2) / slots, 0.8 / slots
        elevation_info = _add_elevation(
            buildings, origin, cache_dir, warnings, log, is_cancelled, dem_sampler,
            lambda frac, msg=None: progress(None if frac is None else base + span * frac, msg))

    roofs.attach(buildings, estimate_roofs)

    preview = [_preview_feature(b, proj, b["_latlon"], b["_holesLatlon"]) for b in buildings]

    for b in buildings:
        for key in [k for k in b if k.startswith("_")]:
            del b[key]

    xs = [p[0] for p in sel]
    zs = [p[1] for p in sel]
    export = {
        "format": FORMAT,
        "version": VERSION,
        "generator": GENERATOR,
        "createdUtc": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "projection": "local-enu-wgs84",
        "origin": {"lat": round(origin[0], 8), "lon": round(origin[1], 8)},
        "area": {
            "polygon": [round(v, 8) for p in ring for v in p],
            "areaKm2": round(sel_area_km2, 4),
            "bounds": {"minX": _r(min(xs)), "minZ": _r(min(zs)), "maxX": _r(max(xs)), "maxZ": _r(max(zs))},
        },
        "sources": [{"name": s, "count": counts[s], "attribution": ATTRIBUTION[s]} for s in steps],
        "warnings": warnings,
        "buildings": buildings,
    }
    if conflict_info:
        export["conflicts"] = conflict_info
    if elevation_info:
        export["elevation"] = elevation_info
    export["roofInfo"] = roofs.export_info(buildings, estimate_roofs)
    progress(1.0, "Done")
    return export, preview


def _add_elevation(buildings, origin, cache_dir, warnings, log, is_cancelled, dem_sampler, progress):
    """Adds groundElevation (m above sea level) and y (m above the origin's ground) to each building.

    Returns the export's "elevation" block, or None when no elevation could be had.
    A failure only costs the elevation; the buildings are still exported.
    """
    if not buildings:
        return None  # nothing to place on the ground
    failed = "Ground elevation could not be loaded, so the result has no heights above sea level. Try again later."
    points = [origin] + [(b["lat"], b["lon"]) for b in buildings]
    attribution = None
    try:
        if dem_sampler is not None:
            values, sources, attribution = [dem_sampler(la, lo) for la, lo in points], [terrain.NAME], [terrain.ATTRIBUTION]
        else:
            values, sources = elevation_api.fetch(points, cache_dir, log, progress, is_cancelled)
    except Cancelled:
        raise
    except Exception as e:
        warnings.append(failed)
        log(f"WARNING: elevation failed: {e}")
        return None
    origin_ground, ground = values[0], values[1:]
    found = [g for g in ground if g is not None]
    if not found:
        warnings.append(failed)
        return None
    if len(found) < len(ground):
        warnings.append(f"{len(ground) - len(found)} building(s) have no ground elevation (no data there), "
                        "so they have no groundElevation / y fields.")
    for b, g in zip(buildings, ground):
        if g is None:
            continue
        b["groundElevation"] = _r(g, 1)
        if origin_ground is not None:
            b["y"] = _r(g - origin_ground, 1)
    log(f"Elevation: ground {min(found):.0f} to {max(found):.0f} m above sea level ({', '.join(sources)})")
    info = {
        "sources": sources,
        "attribution": attribution or [p.attribution for p in elevation_api.PROVIDERS if p.label in sources],
        "minGround": _r(min(found), 1),
        "maxGround": _r(max(found), 1),
    }
    if origin_ground is not None:
        info["originGround"] = _r(origin_ground, 1)
    return info


def preview_from_export(export):
    """Rebuilds map preview features from a saved export (outlines from footprint offsets)."""
    o = export["origin"]
    proj = geo.EnuProjection(o["lat"], o["lon"])
    features = []
    for b in export["buildings"]:
        to_lonlat = _offset_to_lonlat(b, proj)
        fp = b.get("footprint") or []
        ring = [to_lonlat(fp[i], fp[i + 1]) for i in range(0, len(fp) - 1, 2)]
        holes = [[to_lonlat(fp[i], fp[i + 1]) for i in range(0, len(fp) - 1, 2)]
                 for fp in b.get("footprintHoles") or []]
        features.append(_preview_feature(b, proj, ring, holes))
    return features


def _preview_feature(b, proj, ring_lonlat, holes_lonlat=None):
    if holes_lonlat is None:
        to_lonlat = _offset_to_lonlat(b, proj)
        holes_lonlat = [[to_lonlat(fp[i], fp[i + 1]) for i in range(0, len(fp) - 1, 2)]
                        for fp in b.get("footprintHoles") or []]
    return {
        "type": "Feature",
        "properties": {
            "id": b["id"], "source": b["source"], "category": b["category"],
            "width": b["width"], "length": b["length"], "rotation": b["rotation"],
            "height": b["height"], "levels": b["levels"], "heightSource": b["heightSource"],
            "groundElevation": b.get("groundElevation"), "y": b.get("y"),
            "area": b["area"], "name": b.get("name", ""), "buildingTag": b.get("buildingTag", "yes"),
            "mappedAppearance": b.get("mappedAppearance"), "visualDetails": b.get("visualDetails"),
            "roof": b.get("roof"),
            "condition": b.get("condition"),
            "footprintGeometryComplete": b.get("footprintGeometryComplete") is True,
            "footprintAdjusted": b.get("footprintAdjusted"),
        },
        "geometry": {"type": "Polygon", "coordinates": [ring_lonlat + ring_lonlat[:1]]
                     + [hole + hole[:1] for hole in holes_lonlat]},
        "box": _box_lonlat(b, proj),
    }


def _offset_to_lonlat(b, proj):
    """Returns f(dx, dy) -> [lon, lat] for metre offsets from the building centre.

    Uses the local inverse Jacobian of the projection, exact to millimetres
    over the size of a building.
    """
    clat, clon = b["lat"], b["lon"]
    x0, y0 = proj.forward(clat, clon)
    d = 1e-4
    xa, ya = proj.forward(clat + d, clon)
    xb, yb = proj.forward(clat, clon + d)
    j11, j12, j21, j22 = (xa - x0) / d, (xb - x0) / d, (ya - y0) / d, (yb - y0) / d  # d(x,y)/d(lat,lon)
    det = j11 * j22 - j12 * j21

    def to_lonlat(dx, dy):
        dlat = (j22 * dx - j12 * dy) / det
        dlon = (-j21 * dx + j11 * dy) / det
        return [round(clon + dlon, 7), round(clat + dlat, 7)]
    return to_lonlat


def _box_lonlat(b, proj):
    """Corners of the oriented box Unity places, as [[lon, lat], ...] (closed)."""
    rot = math.radians(b["rotation"])
    # Length axis points along the bearing (clockwise from north), width across it.
    lx, ly = math.sin(rot), math.cos(rot)
    wx, wy = ly, -lx
    hl, hw = b["length"] / 2, b["width"] / 2
    to_lonlat = _offset_to_lonlat(b, proj)
    return [to_lonlat(sl * hl * lx + sw * hw * wx, sl * hl * ly + sw * hw * wy)
            for sl, sw in ((1, 1), (1, -1), (-1, -1), (-1, 1), (1, 1))]


DUPLICATE_COVERAGE = 0.3  # share of an ML outline covered by OSM buildings that marks it a duplicate


def _contains(item, x, y):
    box = item["_box"]
    return (box[0] <= x <= box[2] and box[1] <= y <= box[3] and geo.point_in_ring(x, y, item["_ring"])
            and not any(geo.point_in_ring(x, y, hole) for hole in item.get("_holes", [])))


def _is_duplicate(ms, candidates):
    """True when an ML outline is the same building as one already in OSM.

    The two datasets are often offset by a few metres, so beyond the cheap
    centroid tests we sample the ML outline and measure how much of it OSM covers.
    """
    candidates = [o for o in candidates
                  if o["_box"][0] <= ms["_box"][2] and ms["_box"][0] <= o["_box"][2]
                  and o["_box"][1] <= ms["_box"][3] and ms["_box"][1] <= o["_box"][3]]
    if not candidates:
        return False
    mx, my = ms["_centroid"]
    for o in candidates:
        if (_contains(o, mx, my)
                or (_contains(o, *o["_centroid"]) and _contains(ms, *o["_centroid"]))):
            return True

    samples = _samples(ms)
    if not samples:
        return False
    covered = sum(1 for x, y in samples if any(_contains(o, x, y) for o in candidates))
    return covered / len(samples) >= DUPLICATE_COVERAGE


def _samples(item, n=6):
    """n x n grid over the oriented box, kept where it falls inside the outline."""
    rot = math.radians(item["rotation"])
    lx, ly = math.sin(rot), math.cos(rot)
    wx, wy = ly, -lx
    pts = []
    for i in range(n):
        u = ((i + 0.5) / n - 0.5) * item["length"]
        for j in range(n):
            v = ((j + 0.5) / n - 0.5) * item["width"]
            x = item["x"] + u * lx + v * wx
            y = item["z"] + u * ly + v * wy
            if _contains(item, x, y):
                pts.append((x, y))
    return pts


def _reshape(item, pts, proj):
    """Gives a processed building a new outline (moved or cut by conflicts.py) and everything that follows from
    it: the oriented box, position, area and footprint. A courtyard no longer inside goes, and with it the claim
    that the outline is complete."""
    old_holes = item.get("_holes", [])
    holes = [h for h in old_holes if all(geo.point_in_ring(x, y, pts) for x, y in h)]
    if len(holes) != len(old_holes):
        item["footprintGeometryComplete"] = False
    area = geo.signed_area(pts) - sum(abs(geo.signed_area(h)) for h in holes)
    ox, oy, width, length, rotation = geo.min_area_rect(pts)
    olat, olon = proj.inverse(ox, oy)

    def lonlat(x, y):
        lat, lon = proj.inverse(x, y)
        return [round(lon, 8), round(lat, 8)]
    item.update({
        "lat": round(olat, 8), "lon": round(olon, 8), "x": _r(ox), "z": _r(oy),
        "width": _r(width), "length": _r(length), "rotation": _r(rotation), "area": _r(area, 1),
        "footprint": [_r(v) for p in pts for v in (p[0] - ox, p[1] - oy)],
        "_ring": pts, "_holes": holes, "_box": geo.bbox(pts), "_centroid": geo.centroid(pts),
        "_latlon": [lonlat(x, y) for x, y in pts],
        "_holesLatlon": [[lonlat(x, y) for x, y in hole] for hole in holes],
    })
    if holes:
        item["footprintHoles"] = [[_r(v) for p in hole for v in (p[0] - ox, p[1] - oy)] for hole in holes]
    else:
        item.pop("footprintHoles", None)


def _process(b, proj, sel, min_area):
    pts = [proj.forward(lat, lon) for lat, lon in b["ring"]]
    pts = geo.clean_ring(pts)
    if len(pts) < 3:
        return None
    area = geo.signed_area(pts)
    if area < 0:
        pts.reverse()
        area = -area
    holes = []
    complete = b.get("geometryComplete") is True
    for ring in b.get("holes") or []:
        hole = geo.clean_ring([proj.forward(lat, lon) for lat, lon in ring])
        if len(hole) < 3 or not all(geo.point_in_ring(x, y, pts) for x, y in hole):
            complete = False
            continue
        hole_area = geo.signed_area(hole)
        if abs(hole_area) < 0.01:
            complete = False
            continue
        if hole_area > 0:
            hole.reverse()
        holes.append(hole)
        area -= abs(hole_area)
    if area < min_area:
        return "small"
    cx, cy = geo.centroid(pts)
    if not geo.point_in_ring(cx, cy, sel):
        return "outside"

    ox, oy, width, length, rotation = geo.min_area_rect(pts)
    tags = b.get("tags", {})
    category, category_source = classify.categorize(tags, area, width, length)
    height, levels, height_source = classify.estimate_height(tags, category, b.get("ms_height"))

    # Position = oriented-box centre, so boxes and prefabs land centred on the footprint.
    olat, olon = proj.inverse(ox, oy)
    latlon = [[lon, lat] for lat, lon in b["ring"]]
    result = {
        "id": b["id"],
        "source": b["source"],
        "category": category,
        "categorySource": category_source,
        "buildingTag": tags.get("building", "yes"),
        "name": tags.get("name", ""),
        "lat": round(olat, 8),
        "lon": round(olon, 8),
        "x": _r(ox),
        "z": _r(oy),
        "width": _r(width),
        "length": _r(length),
        "rotation": _r(rotation),
        "area": _r(area, 1),
        "height": _r(height, 1),
        "levels": levels,
        "heightSource": height_source,
        "footprint": [_r(v) for p in pts for v in (p[0] - ox, p[1] - oy)],
        "footprintGeometryComplete": complete,
        "_ring": pts,
        "_holes": holes,
        "_box": geo.bbox(pts),
        "_centroid": (cx, cy),
        "_latlon": latlon,
        "_holesLatlon": [[list(reversed(proj.inverse(x, y))) for x, y in hole] for hole in holes],
    }
    if holes:
        result["footprintHoles"] = [[_r(v) for p in hole for v in (p[0] - ox, p[1] - oy)] for hole in holes]
    mapped = appearance.from_tags(tags, b["id"]) if b["source"] == "osm" else None
    if mapped:
        result["mappedAppearance"] = mapped
    mapped_condition = condition.from_tags(tags) if b["source"] == "osm" else None
    if mapped_condition:
        result["mappedCondition"] = mapped_condition
    return result
