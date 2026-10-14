"""Preserve mapped architectural attributes without inventing missing ones."""
import math
import re

COLORS = {
    "black": "#000000", "white": "#ffffff", "gray": "#808080", "grey": "#808080",
    "silver": "#c0c0c0", "red": "#ff0000", "maroon": "#800000", "brown": "#a52a2a",
    "green": "#008000", "darkgreen": "#006400", "olive": "#808000", "blue": "#0000ff",
    "navy": "#000080", "teal": "#008080", "yellow": "#ffff00", "orange": "#ffa500",
    "beige": "#f5f5dc", "tan": "#d2b48c", "pink": "#ffc0cb", "purple": "#800080",
    "lightgray": "#d3d3d3", "lightgrey": "#d3d3d3", "darkgray": "#a9a9a9", "darkgrey": "#a9a9a9",
}
NUMERIC = re.compile(r"^([+-]?\d+(?:[.,]\d+)?)\s*(m|metres?|meters?|ft|feet)?$", re.I)
COMPASS = {name: i * 22.5 for i, name in enumerate(
    ("N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"))}


def colour(value):
    if not isinstance(value, str):
        return None
    value = value.strip().lower()
    if re.fullmatch(r"#[0-9a-f]{6}", value):
        return value
    if re.fullmatch(r"#[0-9a-f]{3}", value):
        return "#" + "".join(c * 2 for c in value[1:])
    return COLORS.get(value)


def number(value, minimum, maximum, units=False):
    if not isinstance(value, str):
        return None
    match = NUMERIC.fullmatch(value.strip())
    if not match or (match[2] and not units):
        return None
    value = float(match[1].replace(",", "."))
    if match[2] and match[2].lower() in ("ft", "feet"):
        value *= 0.3048
    return round(value, 3) if math.isfinite(value) and minimum <= value <= maximum else None


def from_tags(tags, source_id):
    """Return optional mappedAppearance. Retain unsupported/raw values as evidence.

    roofDirection means downslope bearing, not ridge bearing. Unknown roof types
    remain in the export; a renderer must not silently call them gabled or flat.
    """
    tag_keys = {"roof:shape", "roof:colour", "roof:color", "roof:material", "roof:height", "roof:levels",
                "roof:direction", "roof:orientation", "roof:angle", "building:material", "building:colour",
                "building:color", "building:use", "building:architecture", "start_date", "min_height", "building:min_level"}
    raw = {k: v.strip()[:500] for k, v in tags.items() if k in tag_keys and isinstance(v, str) and v.strip()}
    if not raw:
        return None
    result = {"source": "osm", "sourceUrl": "https://www.openstreetmap.org/" + source_id.removeprefix("osm/").split("#")[0],
              "attribution": "OpenStreetMap contributors", "license": {"name": "ODbL 1.0", "url": "https://opendatacommons.org/licenses/odbl/1-0/"},
              "tags": raw}
    for key, field in (("roof:shape", "roofShape"), ("roof:material", "roofMaterial"),
                       ("building:material", "wallMaterial"), ("building:use", "buildingUse"),
                       ("building:architecture", "architecture"), ("start_date", "startDate")):
        if key in raw:
            result[field] = raw[key].lower() if key != "start_date" else raw[key]
    for field, preferred, alternative in (("roofColor", "roof:colour", "roof:color"), ("wallColor", "building:colour", "building:color")):
        value = raw.get(preferred) or raw.get(alternative)
        if value:
            result[field + "Raw"] = value
            normalized = colour(value)
            if normalized:
                result[field] = normalized
    for key, field, lo, hi, units in (("roof:height", "roofHeight", 0, 100, True), ("roof:levels", "roofLevels", 0, 30, False),
                                     ("roof:angle", "roofAngle", 0, 90, False), ("min_height", "minHeight", 0, 600, True),
                                     ("building:min_level", "minLevel", 0, 200, False)):
        value = number(raw.get(key), lo, hi, units)
        if value is not None:
            result[field] = value
    direction = raw.get("roof:direction", "").upper()
    bearing = COMPASS.get(direction)
    if bearing is None:
        bearing = number(direction, -3600, 3600)
    if bearing is not None:
        result["roofDirection"] = bearing % 360
        result["roofDirectionMeaning"] = "downslope_bearing"
    if raw.get("roof:orientation") in ("along", "across"):
        result["roofOrientation"] = raw["roof:orientation"]
    return result


def summary(buildings):
    details = [b.get("mappedAppearance") or {} for b in buildings]
    return {"withMappedDetails": sum(bool(d) for d in details),
            "roofShape": sum(bool(d.get("roofShape")) for d in details),
            "roofColor": sum(bool(d.get("roofColor")) for d in details),
            "roofMaterial": sum(bool(d.get("roofMaterial")) for d in details),
            "wallColor": sum(bool(d.get("wallColor")) for d in details),
            "wallMaterial": sum(bool(d.get("wallMaterial")) for d in details)}
