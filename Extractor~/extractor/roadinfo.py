"""What a road is made of and how worn it is, beyond the ground-map material: a finer surface kind
(concrete slabs and cobbles are not the same thing), a wear estimate, and which Ukrainian road network
class the road belongs to. Game engines use these to pick a texture and how weathered to draw it.

Pure functions, no network. The numbers are estimates: OpenStreetMap tags the surface of a minority of
roads and the smoothness of fewer, so most roads get a typical value for their class.
"""
from . import classify, materials

# surface=* -> finer kind. The ground map still uses materials.SURFACE (asphalt / paving / gravel / dirt road).
SURFACE_KINDS = {
    "asphalt": "asphalt", "paved": "asphalt", "chipseal": "asphalt", "bitumen": "asphalt",
    "concrete": "concrete", "concrete:plates": "concrete_plates", "concrete:lanes": "concrete_plates",
    "paving_stones": "paving_stones", "bricks": "paving_stones",
    "sett": "cobblestone", "cobblestone": "cobblestone", "unhewn_cobblestone": "cobblestone",
    "gravel": "gravel", "fine_gravel": "gravel", "compacted": "gravel", "pebblestone": "gravel",
    "crushed_stone": "gravel",
    "ground": "dirt", "dirt": "dirt", "earth": "dirt", "unpaved": "dirt", "mud": "dirt", "clay": "dirt",
    "sand": "dirt", "grass": "dirt", "grass_paver": "dirt",
}
# highway=* values drawn as paths rather than roads (a track still counts as a road: see is_road)
PATH_TYPES = ("footway", "path", "cycleway", "bridleway", "steps", "pedestrian", "track")
# An unpaved street or lane without a width tag is one wheel track, not a town street of its class: in Lyman the
# class default (5.5 m for a residential street) ran through the gardens on both sides of dirt lanes.
UNPAVED_WIDTH = 3.5


def unpaved(tags, highway):
    """A dirt or gravel road by its tags: its surface, else a track grade, else being a track."""
    surface = materials.SURFACE.get((tags.get("surface") or "").strip().lower())
    if surface:
        return surface in ("gravel", "dirt_road")
    return highway == "track" or bool(tags.get("tracktype"))


def road_width(tags, highway, default):
    """(width in metres, source): the width tag, else lanes x a lane, else the class default (narrower for an
    unpaved road, source "unpaved")."""
    w = classify.parse_number(tags.get("width") or tags.get("est_width"))
    if w and 0.5 <= w <= 60:
        return w, "tag"
    lanes = classify.parse_number(tags.get("lanes"))
    if lanes and 1 <= lanes <= 12 and highway not in PATH_TYPES:
        return lanes * materials.LANE_WIDTH, "lanes"
    if highway not in PATH_TYPES and unpaved(tags, highway) and default > UNPAVED_WIDTH:
        return UNPAVED_WIDTH, "unpaved"
    return default, "default"


def is_road(highway):
    """A road for vehicles (tracks too), not a footway, path or pedestrian area."""
    return highway not in PATH_TYPES or highway == "track"


# the ground-map material of a road with no surface tag, as a surface kind
MATERIAL_KINDS = {"asphalt": "asphalt", "paving": "paving_stones", "gravel": "gravel", "dirt_road": "dirt",
                  "path": "dirt", "bare": "dirt"}

# smoothness=* -> wear, 0 = like new, 1 = ruined
SMOOTHNESS = {"excellent": 0.05, "good": 0.2, "intermediate": 0.45, "bad": 0.7, "very_bad": 0.85,
              "horrible": 0.95, "very_horrible": 1.0, "impassable": 1.0}
TRACKTYPE = {"grade1": 0.3, "grade2": 0.45, "grade3": 0.6, "grade4": 0.8, "grade5": 0.9}

# Wear of an untagged road, by highway=*. Deliberately on the worn side: most Ukrainian roads have not
# been resurfaced in decades, and back roads and yard drives are the worst.
CLASS_WEAR = {
    "motorway": 0.25, "motorway_link": 0.3, "trunk": 0.3, "trunk_link": 0.35, "primary": 0.4, "primary_link": 0.45,
    "secondary": 0.5, "secondary_link": 0.55, "tertiary": 0.55, "tertiary_link": 0.6, "unclassified": 0.65,
    "residential": 0.6, "living_street": 0.55, "service": 0.7, "road": 0.6, "busway": 0.5, "track": 0.75,
    "raceway": 0.3, "escape": 0.7,
}
# State roads (М, Н) are kept up better than local ones.
NATIONAL_WEAR_SHIFT = {"M": -0.12, "H": -0.08}

# Ukrainian road numbers: М international, Н national, Р regional, Т territorial (state roads), О and С local.
_LETTERS = {"М": "M", "Н": "H", "Р": "P", "Т": "T", "О": "O", "С": "C", "M": "M", "H": "H", "P": "P",
            "T": "T", "O": "O", "C": "C"}
_NATIONAL_ORDER = "MHPTOC"


def national_class(ref):
    """'M', 'H', 'P', 'T', 'O' or 'C' from a ref such as 'М-03', 'Н 26', 'Т-21-05' or 'Н26;Е40'; '' if none.

    Cyrillic and Latin letters are both accepted (OpenStreetMap has both). The highest class wins when
    a road carries several numbers.
    """
    best = ""
    for part in (ref or "").replace(",", ";").split(";"):
        part = part.strip().upper()
        if len(part) >= 2 and part[0] in _LETTERS:
            rest = part[1:].lstrip(" -–‑")
            if rest[:1].isdigit():
                cls = _LETTERS[part[0]]
                if best == "" or _NATIONAL_ORDER.index(cls) < _NATIONAL_ORDER.index(best):
                    best = cls
    return best


def surface_kind(tags, material):
    """(kind, guessed): the surface of a road as a finer kind than its material, and whether it is a guess.

    The tag decides when it names a known surface; otherwise the ground-map material does.
    """
    tagged = SURFACE_KINDS.get((tags.get("surface") or "").strip().lower())
    if tagged:
        return tagged, False
    return MATERIAL_KINDS.get(material, "asphalt"), True


def wear(tags, highway, national):
    """(wear 0..1, source): from smoothness, then tracktype, then a typical value for the class."""
    smooth = SMOOTHNESS.get((tags.get("smoothness") or "").strip().lower())
    if smooth is not None:
        return smooth, "smoothness"
    if highway == "track" and tags.get("tracktype") in TRACKTYPE:
        return TRACKTYPE[tags["tracktype"]], "tracktype"
    base = CLASS_WEAR.get(highway, 0.6) + NATIONAL_WEAR_SHIFT.get(national, 0.0)
    return max(0.0, min(1.0, base)), "class"


class Settlement:
    """How built-up the surroundings of a point are, from the positions of the buildings (metres)."""

    CELL = 50.0

    def __init__(self, points):
        self.cells = {}
        for x, z in points:
            key = (int(x // self.CELL), int(z // self.CELL))
            self.cells[key] = self.cells.get(key, 0) + 1

    def count(self, x, z, radius=50.0):
        """Buildings in the grid cells within `radius` metres of the point, rounded up to whole cells."""
        r = int(-(-radius // self.CELL))
        cx, cz = int(x // self.CELL), int(z // self.CELL)
        return sum(self.cells.get((cx + i, cz + j), 0) for i in range(-r, r + 1) for j in range(-r, r + 1))

    def is_settled(self, x, z, minimum=6):
        return self.count(x, z) >= minimum
