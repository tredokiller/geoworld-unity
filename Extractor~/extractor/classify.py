"""Building category and height estimation.

Edit the tables below to tune the defaults for your game.
"""
import re

METRES_PER_LEVEL = 3.0

# OSM building=* value -> category used for prefab matching in Unity.
CATEGORY_BY_TAG = {
    "house": "house", "detached": "house", "semidetached_house": "house",
    "bungalow": "house", "cabin": "house", "terrace": "house", "farm": "house",
    "apartments": "apartments", "dormitory": "apartments", "hotel": "apartments",
    "industrial": "industrial", "warehouse": "industrial", "factory": "industrial",
    "manufacture": "industrial", "hangar": "industrial", "storage_tank": "industrial",
    "silo": "industrial",
    "commercial": "commercial", "retail": "commercial", "office": "commercial",
    "supermarket": "commercial", "kiosk": "commercial",
    "school": "public", "kindergarten": "public", "university": "public",
    "college": "public", "hospital": "public", "public": "public", "civic": "public",
    "government": "public", "train_station": "public", "transportation": "public",
    "fire_station": "public", "sports_hall": "public", "stadium": "public",
    "church": "religious", "chapel": "religious", "cathedral": "religious",
    "mosque": "religious", "synagogue": "religious", "temple": "religious",
    "religious": "religious", "monastery": "religious",
    "garage": "garage", "garages": "garage", "carport": "garage", "parking": "garage",
    "shed": "shed", "hut": "shed", "toilets": "shed",
    "barn": "farm", "cowshed": "farm", "farm_auxiliary": "farm", "stable": "farm",
    "greenhouse": "farm", "sty": "farm",
    "service": "utility", "transformer_tower": "utility", "water_tower": "utility",
    "ruins": "ruins", "construction": "construction",
}

# category -> (default levels, default height in metres incl. roof)
DEFAULTS = {
    "house": (1, 5.0),
    "apartments": (5, 15.5),
    "industrial": (1, 8.0),
    "commercial": (2, 7.0),
    "public": (3, 11.0),
    "religious": (1, 15.0),
    "garage": (1, 3.0),
    "shed": (1, 3.0),
    "farm": (1, 5.5),
    "utility": (1, 4.0),
    "ruins": (1, 3.0),
    "construction": (1, 6.0),
    "other": (1, 5.0),
}


def categorize(tags, area, width, length):
    """Returns (category, category_source)."""
    value = (tags.get("building") or "yes").strip().lower()
    if value == "residential":
        return ("apartments" if area > 300 else "house"), "tag"
    if value in CATEGORY_BY_TAG:
        return CATEGORY_BY_TAG[value], "tag"
    # Unknown type (building=yes, all Microsoft outlines): guess from shape.
    if area < 35:
        return "shed", "guessed"
    if area < 300:
        return "house", "guessed"
    if 9.0 <= width <= 18.0 and length / max(width, 1e-6) >= 2.5:
        return "apartments", "guessed"
    return "industrial", "guessed"


_NUMBER = re.compile(r"-?\d+(?:[.,]\d+)?")


def _parse_number(text):
    if not text:
        return None
    m = _NUMBER.search(str(text))
    if not m:
        return None
    value = float(m.group(0).replace(",", "."))
    if "'" in text or "ft" in text:
        value *= 0.3048
    return value


parse_number = _parse_number


def estimate_height(tags, category, ms_height=None):
    """Returns (height_m, levels, height_source)."""
    levels = _parse_number(tags.get("building:levels"))
    roof_levels = _parse_number(tags.get("roof:levels")) or 0.0
    height = _parse_number(tags.get("height") or tags.get("building:height"))

    if height and height > 0:
        lv = int(round(levels)) if levels else max(1, int(round(height / METRES_PER_LEVEL)))
        return height, lv, "tag"
    if levels and levels > 0:
        return levels * METRES_PER_LEVEL + roof_levels * 2.0, int(round(levels)), "levels"
    if ms_height:
        return ms_height, max(1, int(round(ms_height / METRES_PER_LEVEL))), "microsoft"
    lv, h = DEFAULTS.get(category, DEFAULTS["other"])
    return h, lv, "estimated"
