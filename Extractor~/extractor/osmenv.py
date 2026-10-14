"""Roads, railways, water, land cover and trees from OpenStreetMap (Overpass API).

Everything is returned in latitude / longitude; environment.py projects it.
"""
from . import geo, materials, osm, props

_RAIL = "|".join(materials.RAILWAYS)
_WATERWAY = "|".join(list(materials.WATERWAYS) + ["riverbank", "dock"])
_NATURAL = "|".join(materials.AREA_RULES["natural"])
_LEISURE = "|".join(materials.AREA_RULES["leisure"])
_AEROWAY = "|".join(materials.AREA_RULES["aeroway"])


def build_query(south, west, north, east):
    b = f"{south:.7f},{west:.7f},{north:.7f},{east:.7f}"
    mp = '["type"="multipolygon"]'
    return (
        f"[out:json][timeout:{osm.QUERY_TIMEOUT}];("
        f'way["highway"]({b});'
        f'way["railway"~"^({_RAIL})$"]({b});'
        f'way["waterway"~"^({_WATERWAY})$"]({b});'
        f'way["natural"~"^({_NATURAL}|tree_row)$"]({b});'
        f'way["landuse"]({b});'
        f'way["leisure"~"^({_LEISURE})$"]({b});'
        f'way["amenity"="parking"]({b});'
        f'way["aeroway"~"^({_AEROWAY})$"]({b});'
        f'way["barrier"="hedge"]({b});'
        f'node["natural"="tree"]({b});'
        + props.query(b) +
        f'relation{mp}["natural"~"^({_NATURAL})$"]({b});'
        f'relation{mp}["landuse"]({b});'
        f'relation{mp}["leisure"~"^({_LEISURE})$"]({b});'
        f'relation{mp}["amenity"="parking"]({b});'
        f'relation{mp}["waterway"~"^(riverbank|dock)$"]({b});'
        ");"
        "out tags geom;"
    )


def fetch(ring_latlon, log, progress, is_cancelled):
    """Returns the parsed features inside the selection's tiles (see parse())."""
    elements = osm.fetch_elements(ring_latlon, build_query, log, progress, is_cancelled, "OSM terrain features")
    features = parse(elements)
    log("OSM: " + ", ".join(f"{len(v)} {k}" for k, v in features.items() if v))
    progress(1.0, "OSM terrain features: done")
    return features


def area_style(tags):
    """(material, priority, 'key=value') for an area-like set of tags, or None if it is not one.

    material is None for zoning polygons that are exported but not painted.
    """
    best = None
    for key, table in materials.AREA_RULES.items():
        value = tags.get(key)
        if value not in table:
            continue
        material, priority = table[value]
        if key in ("leisure", "amenity") and value in ("pitch", "parking"):
            material = _surface_material(tags) or material
        cand = (material, priority, f"{key}={value}")
        if best is None or (cand[0] is not None, cand[1]) > (best[0] is not None, best[1]):
            best = cand
    if best is None and tags.get("highway") and tags.get("area") == "yes":
        road = materials.ROADS.get(tags["highway"])
        if road:
            return (_surface_material(tags) or road[2], 45, f"highway={tags['highway']}")
    return best


def _surface_material(tags):
    return materials.SURFACE.get((tags.get("surface") or "").strip().lower())


def _is_closed(pts):
    return len(pts) >= 4 and pts[0] == pts[-1]


def parse(elements):
    out = {"roads": [], "railways": [], "waterways": [], "areas": [], "trees": [], "tree_rows": [], "hedges": [],
           "props": [], "barriers": []}
    area_way_ids = set()

    for el in elements:
        if el.get("type") != "way":
            continue
        tags = el.get("tags", {})
        pts = [(g["lat"], g["lon"]) for g in (el.get("geometry") or []) if g]
        if len(pts) < 2:
            continue
        item = {"id": f"osm/way/{el['id']}", "tags": tags, "line": pts}
        if tags.get("barrier") in props.BARRIERS and props.above_ground(tags):
            out["barriers"].append(dict(item, kind=tags["barrier"]))

        style = area_style(tags) if _is_closed(pts) else None
        # A closed road is a roundabout or loop, not an area, unless it says area=yes.
        if style and tags.get("highway") and tags.get("area") != "yes":
            style = None
        if style:
            area_way_ids.add(el["id"])
            out["areas"].append({"id": item["id"], "tags": tags, "rings": [pts[:-1]],
                                 "material": style[0], "priority": style[1], "tag": style[2]})
            continue

        highway = tags.get("highway")
        if highway:
            if materials.ROADS.get(highway) is not None:
                out["roads"].append(item)
        elif tags.get("railway") in materials.RAILWAYS:
            out["railways"].append(item)
        elif tags.get("waterway") in materials.WATERWAYS:
            out["waterways"].append(item)
        elif tags.get("natural") == "tree_row":
            out["tree_rows"].append(item)
        elif tags.get("barrier") == "hedge":
            out["hedges"].append(item)

    for el in elements:
        tags = el.get("tags", {})
        if el.get("type") == "node" and "lat" in el and "lon" in el:
            kind = props.point_kind(tags)
            if kind:
                out["props"].append({"id": f"osm/node/{el['id']}", "tags": tags, "kind": kind,
                                     "pos": (el["lat"], el["lon"])})
        if el.get("type") == "node" and tags.get("natural") == "tree" and "lat" in el:
            out["trees"].append({"id": f"osm/node/{el['id']}", "tags": tags, "pos": (el["lat"], el["lon"])})
        elif el.get("type") == "relation":
            style = area_style(tags)
            if not style:
                continue
            for k, rings in enumerate(_multipolygon(el, area_way_ids)):
                suffix = f"#{k + 1}" if k else ""
                out["areas"].append({"id": f"osm/relation/{el['id']}{suffix}", "tags": tags, "rings": rings,
                                     "material": style[0], "priority": style[1], "tag": style[2]})
    return out


def _multipolygon(el, skip_way_ids):
    """Polygons of a multipolygon relation: [[outer, hole, hole...], ...] in (lat, lon)."""
    outers, inners = [], []
    for m in el.get("members", []):
        if m.get("type") != "way":
            continue
        seg = [(g["lat"], g["lon"]) for g in (m.get("geometry") or []) if g]
        if len(seg) < 2:
            continue
        role = m.get("role")
        if role == "inner":
            inners.append(seg)
        elif role in ("outer", ""):
            if m.get("ref") in skip_way_ids:
                continue  # already drawn as its own tagged way
            outers.append(seg)
    outer_rings = osm._assemble_rings(outers)
    inner_rings = osm._assemble_rings(inners)
    polygons = [[ring] for ring in outer_rings]
    for hole in inner_rings:
        px, py = hole[0][1], hole[0][0]
        for poly in polygons:
            outer = [(lon, lat) for lat, lon in poly[0]]
            if geo.point_in_ring(px, py, outer):
                poly.append(hole)
                break
    return polygons
