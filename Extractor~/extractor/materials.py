"""Ground material classes and the tables that map map data onto them.

Edit the tables to tune the result for your game. The ids are written into
materials.png and environment.json, so keep them stable once you have content.
"""

# id, name, preview colour, what it means
MATERIALS = [
    (0, "none", "#000000", "No data"),
    (1, "grass", "#8fc05a", "Grass, meadow, lawn, park"),
    (2, "cropland", "#e6cb6e", "Ploughed or cultivated field"),
    (3, "forest", "#2f6b3a", "Tree cover: forest floor under trees"),
    (4, "scrub", "#7f9d4c", "Shrubs, heath, rough ground"),
    (5, "bare", "#b8a07a", "Bare soil, sparse vegetation, building sites"),
    (6, "sand", "#efe0a2", "Sand, beach"),
    (7, "rock", "#8b8b8b", "Rock, scree, cliffs"),
    (8, "wetland", "#5fa8a0", "Marsh, wet meadow"),
    (9, "water", "#3b78c8", "Lakes, ponds, rivers, reservoirs"),
    (10, "urban", "#a6a6a6", "Built-up ground between buildings: yards, concrete, mixed paving"),
    (11, "asphalt", "#3e3e44", "Asphalt road or car park"),
    (12, "paving", "#cdbba9", "Concrete slabs, paving stones, cobbles"),
    (13, "gravel", "#bdae93", "Gravel or compacted road"),
    (14, "dirt_road", "#8a6a45", "Dirt or unpaved road, field track"),
    (15, "path", "#b08f5c", "Footpath, trail"),
    (16, "rail", "#5b4b44", "Railway ballast"),
    (17, "building", "#d9534f", "Under a building footprint"),
    (18, "snow", "#ffffff", "Snow and ice"),
    (19, "orchard", "#66a84b", "Orchard or vineyard"),
]
ID = {name: i for i, name, _, _ in MATERIALS}

# ESA WorldCover class code -> material
WORLDCOVER = {
    10: "forest", 20: "scrub", 30: "grass", 40: "cropland", 50: "urban", 60: "bare",
    70: "snow", 80: "water", 90: "wetland", 95: "wetland", 100: "grass",
}

# OSM area tags -> (material, priority). Higher priority is painted later, so it wins.
# A material of None means the tag is exported in environment.json but never painted:
# zoning polygons say what a place is used for, not what the ground looks like.
AREA_RULES = {
    "landuse": {
        "farmland": ("cropland", 20), "allotments": ("cropland", 20), "plant_nursery": ("cropland", 20),
        "meadow": ("grass", 20), "grass": ("grass", 20), "village_green": ("grass", 20),
        "recreation_ground": ("grass", 20), "greenfield": ("grass", 20), "flowerbed": ("grass", 20),
        "cemetery": ("grass", 22),
        "forest": ("forest", 25), "orchard": ("orchard", 25), "vineyard": ("orchard", 25),
        "quarry": ("bare", 22), "landfill": ("bare", 22), "brownfield": ("bare", 22), "construction": ("bare", 22),
        "basin": ("water", 50), "reservoir": ("water", 50), "salt_pond": ("water", 50),
        "railway": ("gravel", 22),
        "residential": (None, 0), "commercial": (None, 0), "industrial": (None, 0), "retail": (None, 0),
        "garages": (None, 0), "farmyard": (None, 0), "military": (None, 0), "education": (None, 0),
        "religious": (None, 0), "institutional": (None, 0), "depot": (None, 0),
    },
    "natural": {
        "wood": ("forest", 30), "scrub": ("scrub", 30), "heath": ("scrub", 30), "fell": ("grass", 30),
        "grassland": ("grass", 30), "wetland": ("wetland", 32), "water": ("water", 50),
        "sand": ("sand", 32), "beach": ("sand", 32), "bare_rock": ("rock", 32), "scree": ("rock", 32),
        "shingle": ("gravel", 32), "mud": ("wetland", 32), "glacier": ("snow", 32),
    },
    "leisure": {
        "park": ("grass", 35), "garden": ("grass", 35), "dog_park": ("grass", 35), "common": ("grass", 35),
        "golf_course": ("grass", 35), "pitch": ("grass", 40), "swimming_pool": ("water", 50),
    },
    "amenity": {"parking": ("asphalt", 45)},
    "aeroway": {"apron": ("asphalt", 45), "runway": ("asphalt", 46), "taxiway": ("asphalt", 46),
                "aerodrome": ("grass", 20)},
    "waterway": {"riverbank": ("water", 50), "dock": ("water", 50)},
}

# Tags that make a closed way an area rather than a line.
AREA_KEYS = tuple(AREA_RULES)

# A pitch or car park takes its look from the surface tag.
SURFACE = {
    "asphalt": "asphalt", "paved": "asphalt", "chipseal": "asphalt", "bitumen": "asphalt",
    "concrete": "paving", "concrete:plates": "paving", "concrete:lanes": "paving",
    "paving_stones": "paving", "sett": "paving", "cobblestone": "paving", "unhewn_cobblestone": "paving",
    "bricks": "paving", "metal": "paving", "wood": "paving", "tartan": "paving", "artificial_turf": "paving",
    "gravel": "gravel", "fine_gravel": "gravel", "compacted": "gravel", "pebblestone": "gravel",
    "crushed_stone": "gravel", "ground": "dirt_road", "dirt": "dirt_road", "earth": "dirt_road",
    "unpaved": "dirt_road", "mud": "dirt_road", "clay": "dirt_road", "sand": "dirt_road",
    "grass": "dirt_road", "grass_paver": "dirt_road",
}

# highway=* -> (rank for paint order, default width in metres, default material)
ROADS = {
    "motorway": (9, 7.5, "asphalt"), "motorway_link": (8, 5.0, "asphalt"),
    "trunk": (9, 7.5, "asphalt"), "trunk_link": (8, 5.0, "asphalt"),
    "primary": (7, 7.0, "asphalt"), "primary_link": (6, 4.5, "asphalt"),
    "secondary": (6, 6.5, "asphalt"), "secondary_link": (5, 4.5, "asphalt"),
    "tertiary": (5, 6.0, "asphalt"), "tertiary_link": (4, 4.0, "asphalt"),
    "unclassified": (4, 5.0, "asphalt"), "residential": (4, 5.5, "asphalt"),
    "living_street": (3, 4.5, "asphalt"), "service": (3, 3.5, "asphalt"),
    "road": (3, 5.0, "asphalt"), "busway": (4, 3.5, "asphalt"),
    "track": (2, 3.0, "dirt_road"),
    "pedestrian": (2, 4.0, "paving"), "footway": (1, 1.6, "path"), "cycleway": (1, 2.0, "asphalt"),
    "bridleway": (1, 2.0, "path"), "path": (1, 1.2, "path"), "steps": (1, 1.6, "paving"),
    "corridor": None, "elevator": None, "platform": (1, 2.5, "paving"), "construction": (2, 4.0, "bare"),
    "proposed": None, "raceway": (3, 8.0, "asphalt"), "bus_guideway": None, "escape": (2, 4.0, "gravel"),
}
LANE_WIDTH = 3.2

TRACK_GRADE = {"grade1": "gravel", "grade2": "gravel", "grade3": "dirt_road", "grade4": "dirt_road", "grade5": "dirt_road"}

# waterway=* -> default width in metres
WATERWAYS = {"river": 14.0, "stream": 2.0, "canal": 8.0, "drain": 2.5, "ditch": 1.5, "tidal_channel": 6.0}

# railway=* -> width of the ballast in metres
RAILWAYS = {"rail": 3.6, "light_rail": 3.2, "narrow_gauge": 3.0, "tram": 2.6, "funicular": 3.0,
            "monorail": 2.0, "preserved": 3.4, "miniature": 1.5}


def palette():
    """[(r, g, b)] indexed by material id."""
    out = []
    for _, _, color, _ in MATERIALS:
        out.append((int(color[1:3], 16), int(color[3:5], 16), int(color[5:7], 16)))
    return out


def legend():
    return [{"id": i, "name": name, "color": color, "description": text} for i, name, color, text in MATERIALS if i]
