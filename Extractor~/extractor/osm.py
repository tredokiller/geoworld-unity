"""Building footprints from OpenStreetMap via the Overpass API."""
import json
import math
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter

from . import geo
from .common import Cancelled, USER_AGENT

# Tried in order until one answers. The public servers are often overloaded,
# so a failure on one is normal.
OVERPASS_ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.openstreetmap.fr/api/interpreter",
    "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
]

QUERY_TIMEOUT = 180
# First pass over the servers gives up quickly so a hung server doesn't stall
# the job; the second pass allows the full query time.
FAST_TIMEOUT = 60

_preferred_server = 0  # index of the last server that answered
# Large selections are split into tiles of at most this many degrees. Bbox
# queries are far cheaper for Overpass than poly:"..." filters; the exact
# polygon test happens locally in the pipeline.
TILE_DEGREES = 0.08


def build_query(south, west, north, east, roads=False):
    """Buildings, and with roads=True the highway lines too (one request: the overlap check needs them)."""
    b = f"{south:.7f},{west:.7f},{north:.7f},{east:.7f}"
    return (
        f"[out:json][timeout:{QUERY_TIMEOUT}];"
        "("
        f'way["building"]["building"!="no"]({b});'
        f'relation["building"]["building"!="no"]["type"="multipolygon"]({b});'
        + (f'way["highway"]({b});' if roads else "") +
        ");"
        "out tags geom;"
    )


def build_road_query(south, west, north, east):
    b = f"{south:.7f},{west:.7f},{north:.7f},{east:.7f}"
    return f'[out:json][timeout:{QUERY_TIMEOUT}];way["highway"]({b});out tags geom;'


def fetch_elements(ring_latlon, make_query, log, progress, is_cancelled, label="OSM"):
    """Runs make_query(south, west, north, east) over the tiles of the selection.

    Returns the unique Overpass elements; one that crosses a tile edge is only kept once.
    """
    tiles = _tiles(ring_latlon)
    log(f"{label}: {len(tiles)} request tile(s)")
    global _preferred_server
    elements = {}
    for i, (s, w, n, e) in enumerate(tiles):
        progress(i / len(tiles), f"{label}: tile {i + 1}/{len(tiles)}")
        data, _preferred_server = _query(make_query(s, w, n, e), _preferred_server, log, progress, is_cancelled)
        for el in data.get("elements", []):
            elements[(el.get("type"), el.get("id"))] = el
    return list(elements.values())


def fetch(ring_latlon, log, progress, is_cancelled, roads=None):
    """Returns raw buildings: dicts with id, source, tags and ring [(lat, lon), ...].

    roads: a list to receive the highway lines of the same request (parse_roads), or None to ask for none.
    """
    query = build_query if roads is None else (lambda s, w, n, e: build_query(s, w, n, e, roads=True))
    elements = fetch_elements(ring_latlon, query, log, progress, is_cancelled)
    buildings = parse(elements)
    if roads is not None:
        roads.extend(parse_roads(elements))
    log(f"OSM: {len(buildings)} building outlines received" + (f", {len(roads)} roads" if roads is not None else ""))
    progress(1.0, "OSM: done")
    return buildings


def fetch_roads(ring_latlon, log, progress, is_cancelled):
    """Highway lines only, for an extraction without OSM buildings."""
    roads = parse_roads(fetch_elements(ring_latlon, build_road_query, log, progress, is_cancelled, "OSM roads"))
    log(f"OSM: {len(roads)} roads received")
    return roads


def parse_roads(elements):
    """Highway ways as {"id", "tags", "line": [(lat, lon), ...]}."""
    out = []
    for el in elements:
        tags = el.get("tags") or {}
        if el.get("type") != "way" or not tags.get("highway"):
            continue
        line = _geometry_points(el.get("geometry") or [])
        if len(line) >= 2:
            out.append({"id": f"osm/way/{el['id']}", "tags": tags, "line": line})
    return out


def _tiles(ring_latlon):
    lats = [p[0] for p in ring_latlon]
    lons = [p[1] for p in ring_latlon]
    south, north, west, east = min(lats), max(lats), min(lons), max(lons)
    ny = max(1, math.ceil((north - south) / TILE_DEGREES))
    nx = max(1, math.ceil((east - west) / TILE_DEGREES))
    dy, dx = (north - south) / ny, (east - west) / nx
    sel = [(lon, lat) for lat, lon in ring_latlon]
    tiles = []
    for iy in range(ny):
        for ix in range(nx):
            t = (south + iy * dy, west + ix * dx, south + (iy + 1) * dy, west + (ix + 1) * dx)
            if nx * ny == 1 or _tile_touches(t, sel):
                tiles.append(t)
    return tiles


def _tile_touches(tile, sel):
    s, w, n, e = tile
    corners = [(w, s), (e, s), (e, n), (w, n)]
    if any(geo.point_in_ring(x, y, sel) for x, y in corners):
        return True
    if any(w <= x <= e and s <= y <= n for x, y in sel):
        return True
    for i in range(len(sel)):
        a, b = sel[i], sel[(i + 1) % len(sel)]
        for j in range(4):
            if _segments_cross(a, b, corners[j], corners[(j + 1) % 4]):
                return True
    return False


def _segments_cross(p1, p2, p3, p4):
    def orient(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    d1, d2 = orient(p3, p4, p1), orient(p3, p4, p2)
    d3, d4 = orient(p1, p2, p3), orient(p1, p2, p4)
    return (d1 > 0) != (d2 > 0) and (d3 > 0) != (d4 > 0)


def _query(query, start_server, log, progress, is_cancelled):
    """Runs a query, rotating through servers. Returns (data, server_index_that_worked)."""
    body = urllib.parse.urlencode({"data": query}).encode()
    errors = []
    for attempt in range(2 * len(OVERPASS_ENDPOINTS)):
        if is_cancelled():
            raise Cancelled()
        idx = (start_server + attempt) % len(OVERPASS_ENDPOINTS)
        url = OVERPASS_ENDPOINTS[idx]
        host = urllib.parse.urlparse(url).netloc
        timeout = FAST_TIMEOUT if attempt < len(OVERPASS_ENDPOINTS) else QUERY_TIMEOUT + 60
        progress(None, f"OSM: waiting for {host}")
        try:
            data = json.loads(_download(url, body, timeout, progress, is_cancelled))
        except Cancelled:
            raise
        except (urllib.error.URLError, TimeoutError, OSError, ValueError) as e:
            errors.append(f"{host}: {e}")
            log(f"OSM: {host} failed ({e}), trying next server")
            if attempt + 1 == len(OVERPASS_ENDPOINTS):
                time.sleep(5)  # every server failed once; give them a moment
            continue
        remark = data.get("remark", "")
        if "error" in remark.lower():
            errors.append(f"{host}: {remark.strip()}")
            log(f"OSM: {host} returned an error ({remark.strip()}), trying next server")
            continue
        return data, idx
    raise RuntimeError("All Overpass servers failed:\n  " + "\n  ".join(errors[-len(OVERPASS_ENDPOINTS):]))


def _download(url, body, timeout, progress, is_cancelled):
    req = urllib.request.Request(url, data=body, headers={
        "User-Agent": USER_AGENT,
        "Content-Type": "application/x-www-form-urlencoded",
    })
    chunks = []
    received = 0
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        while True:
            if is_cancelled():
                raise Cancelled()
            chunk = resp.read(1 << 16)
            if not chunk:
                break
            chunks.append(chunk)
            received += len(chunk)
            progress(None, f"OSM: downloaded {received / 1e6:.1f} MB")
    return b"".join(chunks)


def parse(elements):
    buildings = []
    ways = {}

    for el in elements:
        if el.get("type") != "way":
            continue
        # the request may carry highway lines too: a closed road (a roundabout) is not a building
        if (el.get("tags") or {}).get("building", "no") == "no":
            continue
        geom = el.get("geometry") or []
        ring = _geometry_points(geom)
        if len(ring) >= 4 and ring[0] == ring[-1]:
            building = {
                "id": f"osm/way/{el['id']}",
                "source": "osm",
                "tags": el.get("tags", {}),
                "ring": ring[:-1],
                "holes": [],
                "geometryComplete": len(ring) == len(geom),
            }
            buildings.append(building)
            ways[el["id"]] = building

    for el in elements:
        if el.get("type") != "relation":
            continue
        outer_segments, inner_segments, outer_refs = [], [], set()
        complete = True
        for m in el.get("members", []):
            if m.get("role") not in ("outer", "inner", ""):
                continue
            if m.get("type") != "way":
                complete = False
                continue
            is_inner = m.get("role") == "inner"
            if not is_inner:
                outer_refs.add(m.get("ref"))
            geom = m.get("geometry") or []
            seg = _geometry_points(geom)
            complete = complete and len(seg) == len(geom) and len(seg) >= 2
            if len(seg) >= 2:
                (inner_segments if is_inner else outer_segments).append(seg)
        rings = _assemble_rings(outer_segments)
        holes = _assemble_rings(inner_segments)
        complete = (complete and bool(rings) and _all_segments_closed(outer_segments, rings)
                    and _all_segments_closed(inner_segments, holes))
        assigned_holes = [[] for _ in rings]
        for hole in holes:
            containing = [i for i, ring in enumerate(rings)
                          if all(geo.point_in_ring(lat, lon, ring) for lat, lon in hole)]
            if len(containing) == 1:
                assigned_holes[containing[0]].append(hole)
            else:
                # An unassigned or ambiguous hole means this relation is not a
                # complete roof mask, even when some outer rings can be shown.
                complete = False
        tagged_outers = {ref: ways[ref] for ref in outer_refs if ref in ways}
        for building in tagged_outers.values():
            building["geometryComplete"] = building["geometryComplete"] and complete
        for i, ring in enumerate(rings):
            signature = _ring_signature(ring)
            matching = next((building for building in tagged_outers.values()
                             if _ring_signature(building["ring"]) == signature), None)
            if matching is not None:
                # The existing way remains authoritative for its own tags and
                # ID, while the relation supplies its courtyard geometry.
                known = {_ring_signature(hole) for hole in matching["holes"]}
                matching["holes"].extend(hole for hole in assigned_holes[i]
                                         if _ring_signature(hole) not in known)
                continue
            suffix = f"#{i + 1}" if len(rings) > 1 else ""
            buildings.append({
                "id": f"osm/relation/{el['id']}{suffix}",
                "source": "osm",
                "tags": el.get("tags", {}),
                "ring": ring,
                "holes": assigned_holes[i],
                "geometryComplete": complete,
            })
    return buildings


def _geometry_points(geometry):
    return [(g["lat"], g["lon"]) for g in geometry if isinstance(g, dict)
            and isinstance(g.get("lat"), (int, float)) and isinstance(g.get("lon"), (int, float))
            and math.isfinite(g["lat"]) and math.isfinite(g["lon"])
            and -90 <= g["lat"] <= 90 and -180 <= g["lon"] <= 180]


def _segment_edges(segments):
    return Counter(tuple(sorted((a, b))) for segment in segments for a, b in zip(segment, segment[1:]))


def _ring_signature(ring):
    return tuple(sorted(_segment_edges([ring + ring[:1]]).items()))


def _all_segments_closed(segments, rings):
    return _segment_edges(segments) == _segment_edges([ring + ring[:1] for ring in rings])


def _assemble_rings(segments):
    """Joins open way segments end-to-end into closed rings."""
    rings = []
    open_segs = []
    for seg in segments:
        if seg[0] == seg[-1] and len(seg) >= 4:
            rings.append(seg[:-1])
        else:
            open_segs.append(list(seg))

    while open_segs:
        current = open_segs.pop(0)
        joined = True
        while current[0] != current[-1] and joined:
            joined = False
            for i, seg in enumerate(open_segs):
                if seg[0] == current[-1]:
                    current += seg[1:]
                elif seg[-1] == current[-1]:
                    current += seg[-2::-1]
                elif seg[-1] == current[0]:
                    current = seg[:-1] + current
                elif seg[0] == current[0]:
                    current = seg[:0:-1] + current
                else:
                    continue
                open_segs.pop(i)
                joined = True
                break
        if current[0] == current[-1] and len(current) >= 4:
            rings.append(current[:-1])
    return rings
