"""Buildings that overlap each other or stand on a road.

OpenStreetMap outlines are traced by people; Microsoft's are detected in imagery that is often a few metres
off the OSM roads and buildings. Without a check their outlines overlap the OSM ones (and each other) and slide
onto streets, and a 3D world then has walls through walls and houses in the carriageway.

Two passes over the processed buildings (dicts with "_ring" counter-clockwise, "_box", "source", "id"):
- roads: a Microsoft outline mostly on a road is dropped; one reaching onto it is moved back off it (up to
  MAX_MOVE) or, where moving would hit something, cut along the road edge. OSM outlines are left as mapped;
  the road widths are mostly estimates.
- overlaps: OSM wins over Microsoft, the larger over the smaller within a source. The other one is dropped when
  the winner covers half of it (a duplicate, or a part mapped inside another building), else cut along the
  winner's wall so the two stand wall to wall.

Every change is recorded on the building ("footprintAdjusted": action moved or cut, reason road or overlap,
against the road or building id) and counted in the summary.
"""
import math

from . import geo, materials, roadinfo

ROAD_CLEARANCE = 1.0       # m between a road's edge and a building that was moved or cut off it (eaves and porches
                           # of the Unity home models reach about that far past the outline)
MAX_MOVE = 3.5             # m a building may be moved off a road; further, it is cut instead
ROAD_DROP_SHARE = 0.5      # share of a Microsoft outline on a road that marks it a false or misplaced detection
ROAD_KEEP = 0.6            # share of an outline that must remain after cutting off the road
OVERLAP_MIN = 0.5          # m2 of overlap below which two outlines count as touching
OVERLAP_DROP_SHARE = 0.5   # share of the smaller outline under the winner that marks it a duplicate or a part
OVERLAP_KEEP = 0.5         # share of an outline that must remain after cutting off the overlaps
MIN_WIDTH = 1.5            # m, a thinner remainder is not a building
SAMPLES = 8                # n x n points over a building's bounding box to measure how much of it is on a road


def road_lines(roads, proj):
    """Road segments from parsed OSM highways ({"id", "tags", "line": [(lat, lon), ...]}): (ax, ay, bx, by,
    half width, id). Paths, bridges, tunnels, covered ways and pedestrian areas are left out: buildings may
    stand over or beside them."""
    out = []
    for item in roads:
        tags = item.get("tags", {})
        highway = tags.get("highway")
        spec = materials.ROADS.get(highway)
        if spec is None or not roadinfo.is_road(highway) or tags.get("area") == "yes":
            continue
        if any(tags.get(k, "no") not in ("no", "") for k in ("bridge", "tunnel", "covered", "indoor")):
            continue
        width, _ = roadinfo.road_width(tags, highway, spec[1])
        pts = [proj.forward(lat, lon) for lat, lon in item["line"]]
        for (ax, ay), (bx, by) in zip(pts, pts[1:]):
            if math.hypot(bx - ax, by - ay) > 1e-6:
                out.append((ax, ay, bx, by, width / 2, item["id"]))
    return out


def resolve(buildings, lines, reshape, min_area, log=print):
    """Both passes. lines: road_lines(...) or None when no roads could be loaded (the road pass is skipped).
    reshape(item, ring) gives a building a new outline and everything derived from it.
    Returns (kept buildings, summary)."""
    for b in buildings:
        b["_area0"] = geo.signed_area(b["_ring"])
    summary = {"fixed": True, "roadsChecked": lines is not None, "roadMoved": 0, "roadCut": 0, "roadDropped": 0,
               "osmOnRoad": 0, "overlapCut": 0, "overlapDropped": 0, "dropped": []}
    if lines is not None:
        buildings = resolve_roads(buildings, lines, reshape, min_area, summary)
    buildings = resolve_overlaps(buildings, reshape, min_area, summary)
    for b in buildings:
        b.pop("_area0", None)
    log(f"Overlaps and roads: {summary['overlapCut']} outlines cut and {summary['overlapDropped']} dropped where they "
        f"overlapped; {summary['roadMoved']} moved off roads, {summary['roadCut']} cut back to the road edge, "
        f"{summary['roadDropped']} dropped as mostly on a road"
        + (f"; {summary['osmOnRoad']} OSM buildings on a road left as mapped" if summary["osmOnRoad"] else "")
        + ("" if lines is not None else " (roads not checked: no road data)"))
    return buildings, summary


# ---------------------------------------------------------------- roads

def _index(lines):
    index = geo.GridIndex(50.0)
    for s in lines:
        h = s[4]
        index.insert(s, (min(s[0], s[2]) - h, min(s[1], s[3]) - h, max(s[0], s[2]) + h, max(s[1], s[3]) + h))
    return index


def _near(index, box):
    return list(index.query_box(box))


def _samples(ring, n=SAMPLES):
    x0, y0, x1, y1 = geo.bbox(ring)
    pts = []
    for i in range(n):
        for j in range(n):
            x, y = x0 + (i + 0.5) / n * (x1 - x0), y0 + (j + 0.5) / n * (y1 - y0)
            if geo.point_in_ring(x, y, ring):
                pts.append((x, y))
    return pts or [geo.centroid(ring)]


def _on(x, y, s, margin=0.0):
    return geo.segment_distance(x, y, *s[:4]) < s[4] + margin


def _road_share(ring, segs):
    pts = _samples(ring)
    return sum(1 for x, y in pts if any(_on(x, y, s) for s in segs)) / len(pts)


def _clear(ring, segs):
    """No vertex, edge midpoint or inside sample of the outline on a road."""
    pts = list(ring) + [((ax + bx) / 2, (ay + by) / 2) for (ax, ay), (bx, by) in zip(ring, ring[1:] + ring[:1])]
    return not any(_on(x, y, s, -0.01) for x, y in pts + _samples(ring) for s in segs)


def _away(ring, s):
    """Unit normal of the road segment's line pointing to the building, and how far the building reaches past
    the road edge plus the clearance (0 when it does not)."""
    ax, ay, bx, by, h = s[:5]
    dx, dy = bx - ax, by - ay
    length = math.hypot(dx, dy)
    nx, ny = -dy / length, dx / length
    cx, cy = geo.centroid(ring)
    if nx * (cx - ax) + ny * (cy - ay) < 0:
        nx, ny = -nx, -ny
    need = 0.0
    for x, y in ring:
        t = ((x - ax) * dx + (y - ay) * dy) / (length * length)
        if -h / length <= t <= 1 + h / length:   # beside the segment, not past its ends
            need = max(need, h + ROAD_CLEARANCE - (nx * (x - ax) + ny * (y - ay)))
    return nx, ny, need


def resolve_roads(buildings, lines, reshape, min_area, summary):
    index = _index(lines)
    kept = []
    for b in buildings:
        segs = [s for s in _near(index, b["_box"])]
        ring = b["_ring"]
        if not segs or _clear(ring, segs):
            kept.append(b)
            continue
        if b["source"] == "osm":
            summary["osmOnRoad"] += 1
            kept.append(b)
            continue
        if _road_share(ring, segs) >= ROAD_DROP_SHARE:
            _drop(b, "road", _deepest(ring, segs)[5], summary, "roadDropped")
            continue
        # move straight back off the road it reaches furthest onto
        s = _deepest(ring, segs)
        nx, ny, need = _away(ring, s)
        if 0 < need <= MAX_MOVE:
            moved = geo.translate_ring(ring, nx * need, ny * need)
            box = geo.bbox(moved)
            if _clear(moved, _near(index, box)):
                reshape(b, moved)
                b.setdefault("footprintAdjusted", []).append({"action": "moved", "reason": "road", "against": s[5], "movedM": round(need, 2)})
                summary["roadMoved"] += 1
                kept.append(b)
                continue
        # cut along the edge of every road it is on, the deepest first
        piece, against = ring, []
        for _ in range(4):
            near = _near(index, geo.bbox(piece))
            if _clear(piece, near):
                break
            s = _deepest(piece, near)
            nx, ny, _ = _away(piece, s)
            piece = geo.clean_ring(geo.clip_half_plane(piece, nx, ny, nx * s[0] + ny * s[1] + s[4] + ROAD_CLEARANCE))
            against.append(s[5])
            if len(piece) < 3:
                break
        if not _good(piece, b["_area0"] * ROAD_KEEP, min_area) or not _clear(piece, _near(index, geo.bbox(piece))):
            _drop(b, "road", against[0] if against else s[5], summary, "roadDropped")
            continue
        area0 = b["area"]
        reshape(b, piece)
        b.setdefault("footprintAdjusted", []).append({"action": "cut", "reason": "road", "against": against[0], "areaBefore": area0})
        summary["roadCut"] += 1
        kept.append(b)
    return kept


def _deepest(ring, segs):
    """The road segment the outline reaches furthest onto."""
    def depth(s):
        inside = max(s[4] - geo.segment_distance(x, y, *s[:4]) for x, y in ring + _samples(ring))
        return inside
    return max(segs, key=depth)


# ---------------------------------------------------------------- overlaps

def resolve_overlaps(buildings, reshape, min_area, summary):
    """The winners first: each building is checked against the ones already kept, which all outrank it."""
    order = sorted(buildings, key=lambda b: (b["source"] == "osm", b["_area0"], b["id"]), reverse=True)
    index = geo.GridIndex(50.0)
    kept = []
    for b in order:
        ring, cut_by, dropped = b["_ring"], [], False
        box = b["_box"]
        winners = [w for w in index.query_box(box) if geo.boxes_overlap(box, w["_box"])]
        for w in sorted(winners, key=lambda w: w["id"]):
            for _ in range(3):   # a winner with an L or U outline may take more than one cut
                shared = geo.intersection_area(ring, w["_ring"])
                if shared < OVERLAP_MIN:
                    break
                if shared >= OVERLAP_DROP_SHARE * geo.signed_area(ring):
                    dropped = True
                    break
                piece = _cut(ring, w["_ring"])
                if piece is None:
                    dropped = True
                    break
                ring = piece
                if w["id"] not in cut_by:
                    cut_by.append(w["id"])
            if dropped:
                _drop(b, "overlap", w["id"], summary, "overlapDropped")
                break
        if dropped:
            continue
        if cut_by:
            if not _good(ring, b["_area0"] * OVERLAP_KEEP, min_area):
                _drop(b, "overlap", cut_by[0], summary, "overlapDropped")
                continue
            area0 = b["area"]
            reshape(b, ring)
            b.setdefault("footprintAdjusted", []).append({"action": "cut", "reason": "overlap", "against": cut_by[0], "areaBefore": area0}
                                                         | ({"alsoAgainst": cut_by[1:]} if len(cut_by) > 1 else {}))
            summary["overlapCut"] += 1
        index.insert(b, b["_box"])
        kept.append(b)
    kept.sort(key=lambda b: (b["z"], b["x"]))
    return kept


def _cut(ring, winner):
    """The largest part of ring outside one of the winner's walls (the line through that wall), preferring a
    cut that clears the overlap completely; None when no wall leaves anything."""
    best = None
    n = len(winner)
    for i in range(n):
        ax, ay = winner[i]
        bx, by = winner[(i + 1) % n]
        ex, ey = bx - ax, by - ay
        length = math.hypot(ex, ey)
        if length < 1e-6:
            continue
        nx, ny = ey / length, -ex / length   # outward for a counter-clockwise ring
        piece = geo.clean_ring(geo.clip_half_plane(ring, nx, ny, nx * ax + ny * ay))
        if len(piece) < 3:
            continue
        area = geo.signed_area(piece)
        if area <= 0:
            continue
        clears = geo.intersection_area(piece, winner) < OVERLAP_MIN
        key = (clears, area)
        if best is None or key > best[0]:
            best = (key, piece)
    return best[1] if best else None


# ---------------------------------------------------------------- shared

def _good(ring, keep_area, min_area):
    if len(ring) < 3:
        return False
    area = geo.signed_area(ring)
    if area < max(keep_area, min_area):
        return False
    return geo.min_area_rect(ring)[2] >= MIN_WIDTH


def _drop(b, reason, against, summary, counter):
    summary[counter] += 1
    summary["dropped"].append({"id": b["id"], "reason": reason, "against": against})
