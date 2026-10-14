"""Roof shape, pitch, rise, material and colour of each building, for building generators.

Every value says where it came from, in order of trust:

  reviewed   a person checked it against imagery (visualDetails, added when enriching an export)
  mapped     an OpenStreetMap roof:* tag on the building (mappedAppearance)
  estimated  what is typical for this kind of building in Ukraine. Low confidence; never an
             observation. A generator that wants to show only facts can ignore these.

OSM maps roof details for almost no buildings (3 of 20,971 in Lyman), so without estimates a
generator would have nothing to go on. Estimates are deterministic: the same building id always
gets the same colour, so a re-extraction does not repaint the town.

The roof of building b is b["roof"]; see describe() for the fields.
"""
import math

from . import appearance

MAPPED, REVIEWED, ESTIMATED = "mapped", "reviewed", "estimated"

# Shapes that have a ridge or a single slope and so a direction on the plan.
RIDGE_SHAPES = {"gabled", "hipped", "half-hipped", "hipped_and_gabled", "gambrel", "saltbox", "mansard"}
SLOPED_SHAPES = RIDGE_SHAPES | {"skillion", "pyramidal"}
# The nearest of flat / gabled / hipped, for generators that only build those three.
RENDER_AS = {
    "flat": "flat", "gabled": "gabled", "hipped": "hipped",
    "half-hipped": "hipped", "hipped_and_gabled": "hipped", "pyramidal": "hipped", "mansard": "hipped",
    "gambrel": "gabled", "saltbox": "gabled", "skillion": "flat", "sawtooth": "flat",
    "dome": "hipped", "onion": "hipped", "cone": "hipped", "round": "hipped",
}
SHAPE_ALIASES = {"gable": "gabled", "hip": "hipped", "pitched": None, "mono-pitch": "skillion",
                 "lean_to": "skillion", "monopitch": "skillion", "pyramid": "pyramidal", "half_hipped": "half-hipped"}
DEFAULT_PITCH = {"flat": 0.0, "skillion": 10.0, "gabled": 35.0, "hipped": 28.0, "half-hipped": 30.0,
                 "hipped_and_gabled": 30.0, "pyramidal": 30.0, "gambrel": 40.0, "saltbox": 35.0, "mansard": 60.0}

MATERIAL_ALIASES = {
    "roof_tiles": "clay_tiles", "tiles": "clay_tiles", "tile": "clay_tiles", "clay": "clay_tiles", "clay_tiles": "clay_tiles",
    "metal": "metal", "metal_sheet": "metal", "tin": "metal", "steel": "metal", "zinc": "metal", "aluminium": "metal",
    "tar_paper": "bitumen", "bitumen": "bitumen", "asphalt": "bitumen", "roofing_felt": "bitumen", "felt": "bitumen",
    "concrete": "concrete", "slate": "slate", "asbestos": "asbestos_cement", "eternit": "asbestos_cement",
    "asbestos_cement": "asbestos_cement", "thatch": "thatch", "reed": "thatch", "copper": "copper", "glass": "glass",
    "gravel": "gravel", "stone": "stone", "wood": "wood", "shingle": "wood", "plastic": "plastic",
}
# One representative colour per material, for when only the material is known.
MATERIAL_COLORS = {
    "clay_tiles": "#a34a32", "metal": "#70767b", "slate": "#5f6368", "asbestos_cement": "#7b7d78",
    "bitumen": "#4b4d4f", "concrete": "#8d8f8c", "thatch": "#8a7a52", "copper": "#5f8f7e", "glass": "#6f8fa0",
    "gravel": "#7a7872", "stone": "#7a766e", "wood": "#6d5238", "plastic": "#8a8d90",
}

# (material, colour, weight): what roofs of this kind commonly look like in Ukraine today. Old houses
# have weathered asbestos-cement sheets ("shifer"); newer ones profiled metal in brown, red, green or
# grey; flat blocks bitumen roll roofing, which weathers to dark grey.
PALETTES = {
    "pitched_house": [
        ("asbestos_cement", "#7b7d78", 26), ("asbestos_cement", "#64676a", 8),
        ("metal", "#6e4636", 14), ("metal", "#8a3a2e", 10), ("metal", "#3f6a4a", 7), ("metal", "#70767b", 8),
        ("metal", "#46627f", 3), ("metal", "#8b8e90", 4),
        ("clay_tiles", "#a34a32", 8), ("clay_tiles", "#6a3b2c", 4), ("bitumen", "#4f4a46", 4),
    ],
    "flat_block": [
        ("bitumen", "#4b4d4f", 55), ("bitumen", "#5c625a", 20), ("concrete", "#8d8f8c", 8),
        ("metal", "#6b7075", 7), ("gravel", "#7a7872", 5),
    ],
    "outbuilding": [
        ("metal", "#75797c", 28), ("metal", "#6a4a3b", 18), ("metal", "#3f6a4b", 8), ("bitumen", "#4a4c4e", 24),
        ("asbestos_cement", "#7b7d78", 14), ("metal", "#8a3a2e", 8),
    ],
    "industrial": [
        ("metal", "#8b9195", 40), ("metal", "#b0b4b7", 14), ("metal", "#4f6d8f", 9), ("metal", "#8d3b33", 9),
        ("bitumen", "#55585a", 20), ("asbestos_cement", "#7a7c78", 8),
    ],
    "farm": [
        ("asbestos_cement", "#7b7d78", 32), ("metal", "#767b7f", 24), ("metal", "#6b4535", 14),
        ("metal", "#85392e", 9), ("metal", "#8b8e90", 14), ("clay_tiles", "#a34a32", 7),
    ],
    "public": [
        ("bitumen", "#4b4d4f", 40), ("metal", "#6b7075", 20), ("metal", "#8a3a2e", 12), ("metal", "#3f6a4b", 10),
        ("metal", "#6e4636", 10), ("clay_tiles", "#a34a32", 8),
    ],
}


def _fnv(text):
    h = 2166136261
    for ch in text.encode("utf-8"):
        h = ((h ^ ch) * 16777619) & 0xFFFFFFFF
    return h


def _pick(options, key):
    """Deterministic weighted choice from [(..., weight)] by a string key."""
    total = sum(o[-1] for o in options)
    point = _fnv(key) / 4294967296.0 * total
    for option in options:
        point -= option[-1]
        if point < 0:
            return option
    return options[-1]


def normalize_shape(value):
    if not isinstance(value, str) or not value.strip():
        return None
    value = value.strip().lower().split(";")[0].strip()
    return SHAPE_ALIASES[value] if value in SHAPE_ALIASES else value


def normalize_material(value):
    if not isinstance(value, str) or not value.strip():
        return None
    return MATERIAL_ALIASES.get(value.strip().lower().split(";")[0].strip())


def _spaced_hex(text):
    """'00 80 00' (hex bytes separated by spaces or commas) as '#008000', or None."""
    parts = [p for p in text.replace(",", " ").replace(";", " ").split() if p]
    if len(parts) == 3 and all(len(p) == 2 and all(c in "0123456789abcdefABCDEF" for c in p) for p in parts):
        return "#" + "".join(parts).lower()
    return None


def _estimate_shape(b):
    """(shape, confidence, pitch, palette profile, reason) typical for this building, or None when unknowable."""
    cat = b.get("category")
    levels = b.get("levels") or 1
    area, width, length = b.get("area", 0.0), b.get("width", 0.0), b.get("length", 0.0)
    squarish = length / max(width, 0.1) < 1.2
    if cat == "apartments" or levels >= 4:
        return "flat", 0.75, 2.0, "flat_block", f"{levels}-storey block"
    if cat == "industrial":
        if width >= 30:
            return "flat", 0.45, 2.0, "industrial", "wide industrial hall"
        return "gabled", 0.4, 10.0, "industrial", "industrial building"
    if cat == "commercial":
        return "flat", 0.55, 2.0, "flat_block", "commercial building"
    if cat == "public":
        if levels >= 2:
            return "flat", 0.5, 2.0, "public", f"public building, {levels} floors"
        return "hipped", 0.35, 25.0, "public", "low public building"
    if cat == "farm":
        return "gabled", 0.6, 25.0, "farm", "farm building"
    if cat == "garage":
        if area <= 45:
            return "skillion", 0.4, 8.0, "outbuilding", "garage"
        return "flat", 0.35, 2.0, "outbuilding", "large garage"
    if cat == "shed":
        if area <= 25:
            return "skillion", 0.35, 10.0, "outbuilding", "small shed"
        return "gabled", 0.4, 25.0, "outbuilding", "shed"
    if cat == "utility":
        return "flat", 0.5, 2.0, "outbuilding", "utility building"
    if cat == "house":
        if squarish and area < 180:
            return "hipped", 0.45, 28.0, "pitched_house", "house, near-square footprint"
        return "gabled", 0.55, 35.0, "pitched_house", "house"
    return None  # religious, ruins, construction, other: no useful prior


def _rise(shape, pitch, span, height):
    if shape == "skillion":
        rise = span * math.tan(math.radians(pitch))
    elif shape in SLOPED_SHAPES:
        rise = span / 2.0 * math.tan(math.radians(pitch))
    else:
        return 0.0
    # keep the walls at least 2.4 m and the roof under half the building
    return round(max(0.0, min(rise, height - 2.4, height * 0.5)), 1)


def describe(b, estimate=True):
    """The roof of a building dict as the `roof` block, or None when nothing is known.

    Fields (all optional): shape, shapeSource, shapeConfidence, renderAs (nearest of flat / gabled /
    hipped); ridge ("along" or "across" the long side), ridgeBearing (degrees clockwise from north,
    0..180; assumed to run along the long side unless ridgeSource says "mapped"); slopeBearing
    (downslope, mapped only); pitch (degrees) and rise (roof height above the eaves, m; the walls are
    height - rise), both derived from the shape unless pitchSource / riseSource say "mapped";
    material, materialSource; color (#rrggbb), colorSource, colorConfidence; colorRaw (a mapped
    colour that could not be parsed).
    With estimate=False only mapped and reviewed values appear.
    """
    mapped = b.get("mappedAppearance") or {}
    reviewed = b.get("visualDetails") or {}
    category = b.get("category")
    discount = 0.8 if b.get("categorySource") == "guessed" else 1.0
    width, length, area = b.get("width", 0.0), b.get("length", 0.0), b.get("area", 0.0)
    fill = area / (width * length) if width * length > 0 else 1.0
    key = str(b.get("id", ""))
    roof = {}

    # ---- shape
    shape = src = conf = profile = None
    est_pitch = None
    r_shape, m_shape = normalize_shape(reviewed.get("roofShape")), normalize_shape(mapped.get("roofShape"))
    if r_shape:
        shape, src, conf = r_shape, REVIEWED, 0.8
    elif m_shape:
        shape, src, conf = m_shape, MAPPED, 0.9
    elif estimate:
        guess = _estimate_shape(b)
        if guess:
            shape, conf, est_pitch, profile, _ = guess
            src = ESTIMATED
            conf = conf * discount - (0.1 if fill < 0.7 else 0.0)
    if shape:
        roof.update(shape=shape, shapeSource=src, shapeConfidence=round(max(conf, 0.1), 2))
        if shape in RENDER_AS:
            roof["renderAs"] = RENDER_AS[shape]

    # ---- ridge direction
    rotation = float(b.get("rotation", 0.0))
    if shape in RIDGE_SHAPES:
        ridge, ridge_src = "along", "long_axis"
        bearing = rotation % 180.0
        if mapped.get("roofDirectionMeaning") == "downslope_bearing" and "roofDirection" in mapped:
            bearing, ridge_src = (mapped["roofDirection"] + 90.0) % 180.0, MAPPED
            diff = abs((bearing - rotation + 90.0) % 180.0 - 90.0)
            ridge = "along" if diff <= 45.0 else "across"
        elif mapped.get("roofOrientation") == "across":
            ridge, ridge_src, bearing = "across", MAPPED, (rotation + 90.0) % 180.0
        elif mapped.get("roofOrientation") == "along":
            ridge_src = MAPPED
        roof.update(ridge=ridge, ridgeBearing=round(bearing, 1))
        if ridge_src == MAPPED:
            roof["ridgeSource"] = MAPPED
    elif shape == "skillion" and mapped.get("roofDirectionMeaning") == "downslope_bearing" and "roofDirection" in mapped:
        roof["slopeBearing"] = round(mapped["roofDirection"] % 360.0, 1)

    # ---- pitch and rise
    if shape in SLOPED_SHAPES:
        if isinstance(mapped.get("roofAngle"), (int, float)):
            pitch, pitch_src = float(mapped["roofAngle"]), MAPPED
        else:
            pitch = est_pitch if src == ESTIMATED and est_pitch is not None else DEFAULT_PITCH.get(shape, 30.0)
            pitch_src = ESTIMATED
        roof["pitch"] = round(pitch, 1)
        if pitch_src == MAPPED:
            roof["pitchSource"] = MAPPED
    height = float(b.get("height", 0.0))
    rise = rise_src = None
    if isinstance(mapped.get("roofHeight"), (int, float)):
        rise, rise_src = float(mapped["roofHeight"]), MAPPED
    elif isinstance(mapped.get("roofLevels"), (int, float)) and mapped["roofLevels"] > 0:
        rise, rise_src = float(mapped["roofLevels"]) * 2.0, MAPPED
    elif shape in SLOPED_SHAPES:
        span = length if roof.get("ridge") == "across" else width
        rise, rise_src = _rise(shape, roof["pitch"], span, height), ESTIMATED
    if rise is not None:
        rise = round(max(0.0, min(rise, max(height - 2.0, 0.0))), 1) if rise_src == MAPPED else rise
        roof["rise"] = rise
        if rise_src == MAPPED:
            roof["riseSource"] = MAPPED

    # ---- material and colour
    material, material_src = normalize_material(mapped.get("roofMaterial")), MAPPED
    color = color_src = None
    color_conf = None
    for value, source in ((reviewed.get("roofColor"), REVIEWED), (mapped.get("roofColor"), MAPPED)):
        parsed = appearance.colour(value)
        if parsed:
            color, color_src, color_conf = parsed, source, 0.8 if source == REVIEWED else 0.9
            break
    if color is None and mapped.get("roofColorRaw"):
        odd = _spaced_hex(mapped["roofColorRaw"])
        if odd:
            color, color_src, color_conf = odd, MAPPED, 0.7
        else:
            roof["colorRaw"] = mapped["roofColorRaw"]
    if estimate and color is None:
        palette = PALETTES.get(profile or _profile_of(category, shape))
        if palette:
            options = [p for p in palette if material is None or p[0] == material]
            if options:
                chosen = _pick(options, key + "|roof")
                color, color_src = chosen[1], ESTIMATED
                color_conf = 0.45 if material else 0.25
                if material is None:
                    material, material_src = chosen[0], ESTIMATED
        if color is None and material in MATERIAL_COLORS:
            color, color_src, color_conf = MATERIAL_COLORS[material], ESTIMATED, 0.35
    if material:
        roof.update(material=material, materialSource=material_src)
    if color:
        roof.update(color=color, colorSource=color_src, colorConfidence=round(color_conf, 2))
    return roof or None


def _profile_of(category, shape):
    """Palette for a roof whose shape was mapped or reviewed rather than estimated."""
    if category in ("apartments", "commercial", "utility"):
        return "flat_block"
    if category == "industrial":
        return "industrial"
    if category == "farm":
        return "farm"
    if category in ("garage", "shed"):
        return "outbuilding"
    if category == "public":
        return "public"
    if category in ("house", "other", None):
        if shape == "flat":
            return "flat_block"
        return "pitched_house" if shape else None  # an unknown building kind has no typical colour either
    return None


def attach(buildings, estimate=True):
    """Sets b["roof"] on every building that has something to report; returns how many do."""
    n = 0
    for b in buildings:
        roof = describe(b, estimate)
        if roof:
            b["roof"] = roof
            n += 1
        else:
            b.pop("roof", None)
    return n


def refresh(b, estimate=False):
    """Recomputes one building's roof after reviewed details were attached to it."""
    roof = describe(b, estimate)
    if roof:
        b["roof"] = roof
    else:
        b.pop("roof", None)


def summary(buildings):
    """Counts of roof facts by source, for the UI and the export's roofInfo."""
    out = {"buildings": len(buildings)}
    for field in ("shape", "color", "material"):
        counts = {MAPPED: 0, REVIEWED: 0, ESTIMATED: 0}
        for b in buildings:
            source = (b.get("roof") or {}).get(field + "Source")
            if source in counts:
                counts[source] += 1
        counts["unknown"] = len(buildings) - sum(counts.values())
        out[field] = counts
    shapes = {}
    for b in buildings:
        s = (b.get("roof") or {}).get("shape")
        if s:
            shapes[s] = shapes.get(s, 0) + 1
    out["shapes"] = dict(sorted(shapes.items(), key=lambda kv: -kv[1]))
    return out


def export_info(buildings, estimating):
    return {
        "estimated": bool(estimating),
        "note": ("Roof fields with a Source of 'estimated' are typical values for this kind of building in Ukraine, "
                 "not observations (OpenStreetMap maps roof details for very few buildings). 'mapped' comes from OSM "
                 "roof tags, 'reviewed' from a person checking imagery. Ignore estimated fields to show only facts."),
        "summary": summary(buildings),
    }
