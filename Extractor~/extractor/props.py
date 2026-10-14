"""Mapped street objects and barriers. Missing dimensions remain unknown (zero)."""
import math

from . import classify, geo

POINT_TAGS = {
    "highway": {"street_lamp": "street_lamp", "bus_stop": "bus_stop", "traffic_signals": "traffic_signals"},
    "amenity": {"bench": "bench", "waste_basket": "waste_basket", "waste_disposal": "waste_disposal",
                "bicycle_parking": "bicycle_parking", "shelter": "shelter", "post_box": "post_box"},
    "barrier": {"bollard": "bollard", "gate": "gate", "lift_gate": "gate"},
    "power": {"pole": "power_pole"},
}
BARRIERS = ("fence", "wall", "retaining_wall", "guard_rail", "city_wall")


def query(bbox):
    parts = []
    for key, values in POINT_TAGS.items():
        parts.append(f'node["{key}"~"^({"|".join(values)})$"]({bbox});')
    parts.append(f'node["entrance"]["entrance"!="no"]({bbox});')
    parts.append(f'way["barrier"~"^({"|".join(BARRIERS)})$"]({bbox});')
    return "".join(parts)


def above_ground(tags):
    layer = classify.parse_number(tags.get("layer")) or 0
    level = classify.parse_number(tags.get("level")) or 0
    return (tags.get("indoor") != "yes" and tags.get("location") != "underground"
            and tags.get("tunnel", "no") in ("no", "") and layer >= 0 and level == 0)


def point_kind(tags):
    if not above_ground(tags):
        return None
    if tags.get("entrance", "no") not in ("no", ""):
        return "entrance"
    for key, table in POINT_TAGS.items():
        if tags.get(key) in table:
            return table[tags[key]]
    return None


def dimension(tags, key):
    value = classify.parse_number(tags.get(key))
    return round(value, 2) if value and math.isfinite(value) and 0 < value < 100 else 0.0


def bearing(value):
    """Only explicit numeric/compass bearings; 'forward' requires way context."""
    compass = {v: i * 22.5 for i, v in enumerate(
        ("N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"))}
    if str(value).upper() in compass:
        return compass[str(value).upper()]
    try:
        result = float(value)
        return round(result % 360, 2) if math.isfinite(result) else None
    except (TypeError, ValueError):
        return None


def metadata(item):
    tags = item["tags"]
    return {"id": item["id"], "source": "osm", "placementSource": "mapped",
            "type": item["kind"], "name": tags.get("name", ""),
            "material": tags.get("material", ""), "colour": tags.get("colour", ""),
            "height": dimension(tags, "height"), "width": dimension(tags, "width"),
            "access": tags.get("access", ""), "shelter": tags.get("shelter", ""),
            "direction": tags.get("direction", "")}


def project(features, proj, rect):
    points, barriers = [], []
    for item in features.get("props", []):
        x, z = proj.forward(*item["pos"])
        if not (rect[0] <= x <= rect[2] and rect[1] <= z <= rect[3]):
            continue
        entry = metadata(item)
        entry.update(x=round(x, 2), z=round(z, 2))
        rotation = bearing(item["tags"].get("direction"))
        entry["rotationSource"] = "tag" if rotation is not None else "unknown"
        if rotation is not None:
            entry["rotation"] = rotation
        points.append(entry)
    for item in features.get("barriers", []):
        line = [proj.forward(*p) for p in item["line"]]
        for k, part in enumerate(geo.clip_polyline(line, rect)):
            if len(part) < 2:
                continue
            entry = metadata(item)
            entry["id"] += f"#{k + 1}" if k else ""
            entry["points"] = [round(v, 2) for p in part for v in p]
            entry["fenceType"] = item["tags"].get("fence_type", "")
            barriers.append(entry)
    return points, barriers
