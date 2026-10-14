"""Building condition (war damage): where the evidence comes from, how it is merged, and what is exported.

A building has no condition until something says what state it is in. Evidence comes from
  - reviewed observations (a person looked at imagery or a photo) and reports (news, humanitarian
    assessments) that name a building, a point or an area,
  - OpenStreetMap tags (building=ruins),
  - estimates from satellite radar (see sar.py), which are the weakest kind.
Each piece of evidence carries a state, the date that state was observed or reported, a basis, a
confidence and its source, so the Unity side and a human can always see why a building looks as it does.
A building nobody has evidence for stays "unknown": it is neither shown as intact nor as damaged.

Evidence is kept per area in a catalog: the areas that come with the app (data/condition/<area>) and the
areas the app makes for an export when it estimates its damage from radar (cache/condition/auto-...). Any
export uses the area that covers it best; nothing here is tied to one town.

No network request is made here, except through sar.analyse in estimate_radar; collectors (sar.py, tools/)
write their results into the catalog files.
"""
import copy
import datetime as dt
import hashlib
import json
import math
import re
import threading
import uuid
from pathlib import Path

from . import geo

FORMAT = "building-extractor.condition"
VERSION = 1
STATES = ("intact", "damaged", "heavily_damaged", "destroyed")
SEVERITY = {state: rank for rank, state in enumerate(STATES)}
BASES = ("observed", "reported", "mapped", "estimated")
EXPLICIT = ("observed", "reported", "mapped")
CONFIDENCE = {"low": 1, "medium": 2, "high": 3}
AUTO_MARGIN = 0.002   # degrees (about 150 to 220 m) added around an export for the area made for it
MAX_RADAR_KM2 = 250.0  # a radar estimate of more reads too much and needs too much memory: extract it in parts
RADAR_FRESH_DAYS = 7   # an extraction reuses a radar estimate made this recently
SOURCE_KINDS = ("news", "report", "visual_review", "osm", "satellite", "user")
STATUSES = ("confirmed", "candidate", "rejected")
LOCK = threading.RLock()


def _now():
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def _text(value, limit, name):
    if not isinstance(value, str) or len(value) > limit or any(ord(c) < 32 and c not in "\n\t" for c in value):
        raise ValueError(f"{name} must be text with at most {limit} characters")
    return value.strip()


def _date(value, name, required=False):
    if value in (None, ""):
        if required:
            raise ValueError(f"{name} must be a date (YYYY-MM-DD)")
        return None
    if not isinstance(value, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", value):
        raise ValueError(f"{name} must be a date (YYYY-MM-DD)")
    dt.date.fromisoformat(value)
    return value


def _folder(root, area_id):
    if not isinstance(area_id, str) or not re.fullmatch(r"[a-z0-9-]{1,60}", area_id):
        raise ValueError("Invalid area")
    result = Path(root) / area_id
    if result.resolve().parent != Path(root).resolve():
        raise ValueError("Invalid area")
    return result


def _read_json(path, default):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return default


def _write_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex[:8] + ".tmp")
    try:
        temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


# ------------------------------------------------------------------ catalog

def catalog_folder(data_root, cache_root, area_id):
    """Where an area's catalog.json and radar products are: with the app (data) or made by it (cache)."""
    for root in (data_root, cache_root):
        folder = _folder(root, area_id)
        if (folder / "catalog.json").is_file():
            return folder
    raise FileNotFoundError(f"No condition catalog for the area {area_id}")


def read_catalog(data_root, cache_root, area_id):
    """The catalog of an area plus what the user added or changed in the app.

    Catalog: <data_root>/<area>/catalog.json for the areas that come with the app, <cache_root>/<area>/catalog.json
    for the areas the app made (sources, assessments, rasters).
    User state: <cache_root>/<area>/{sources,assessments}.json; an entry with the id of a catalog one
    replaces it, and an assessment with status "rejected" switches a catalog one off.
    """
    folder = catalog_folder(data_root, cache_root, area_id)
    data = json.loads((folder / "catalog.json").read_text(encoding="utf-8"))
    if data.get("format") != FORMAT or data.get("version") != VERSION:
        raise ValueError("Unsupported condition catalog")
    state = _folder(cache_root, area_id)
    with LOCK:
        sources = {s["id"]: s for s in data.get("sources", [])}
        sources.update({s["id"]: s for s in _read_json(state / "sources.json", [])})
        assessments = {a["id"]: a for a in data.get("assessments", [])}
        assessments.update({a["id"]: a for a in _read_json(state / "assessments.json", [])})
    data["sources"] = list(sources.values())
    data["assessments"] = list(assessments.values())
    data.setdefault("rasters", [])
    data["folder"] = str(folder)
    return data


def validate_source(item):
    if not isinstance(item, dict):
        raise ValueError("A source must be an object")
    kind = item.get("kind", "news")
    if kind not in SOURCE_KINDS:
        raise ValueError("Unsupported source kind")
    result = {"id": _text(item.get("id", ""), 100, "ID") or "src-" + uuid.uuid4().hex[:12], "kind": kind,
              "title": _text(item.get("title", ""), 300, "Title"),
              "publisher": _text(item.get("publisher", ""), 200, "Publisher"),
              "url": _text(item.get("url", ""), 2000, "URL"),
              "publishedAt": _date(item.get("publishedAt"), "Publication date"),
              "note": _text(item.get("note", ""), 2000, "Note")}
    if not re.fullmatch(r"[A-Za-z0-9_.:-]{1,100}", result["id"]):
        raise ValueError("Invalid source ID")
    if not result["title"]:
        raise ValueError("A source needs a title")
    if result["url"] and not re.match(r"https?://[^\s]+$", result["url"]):
        raise ValueError("The URL must be an http or https link")
    license_info = item.get("license")
    if isinstance(license_info, dict) and license_info.get("name"):
        result["license"] = {"name": _text(license_info["name"], 100, "License"),
                             "url": _text(license_info.get("url", ""), 2000, "License URL")}
    if item.get("accessedAt"):
        result["accessedAt"] = _date(item["accessedAt"], "Access date")
    return result


def validate_assessment(item, catalog):
    """Normalise one piece of evidence. Targets are explicit: a building ID, several IDs, a circle or an area."""
    if not isinstance(item, dict):
        raise ValueError("An assessment must be an object")
    result = {"id": _text(item.get("id", ""), 100, "ID") or "asm-" + uuid.uuid4().hex[:12]}
    if not re.fullmatch(r"[A-Za-z0-9_.:-]{1,100}", result["id"]):
        raise ValueError("Invalid assessment ID")
    if item.get("sourceId") not in {s["id"] for s in catalog["sources"]}:
        raise ValueError("Choose a source from this catalog")
    result["sourceId"] = item["sourceId"]
    if item.get("state") not in STATES:
        raise ValueError("State must be intact, damaged, heavily_damaged or destroyed")
    result["state"] = item["state"]
    result["asOf"] = _date(item.get("asOf"), "As-of date", required=True)
    result["basis"] = item.get("basis", "reported")
    if result["basis"] not in EXPLICIT:
        raise ValueError("Basis must be observed, reported or mapped")
    result["confidence"] = item.get("confidence", "medium")
    if result["confidence"] not in CONFIDENCE:
        raise ValueError("Choose a confidence level")
    result["status"] = item.get("status", "confirmed")
    if result["status"] not in STATUSES:
        raise ValueError("Unsupported status")
    result["note"] = _text(item.get("note", ""), 2000, "Note")
    target = item.get("target")
    if not isinstance(target, dict):
        raise ValueError("Choose what this evidence is about")
    mode = target.get("mode")
    if mode == "building":
        result["target"] = {"mode": mode, "buildingId": _text(target.get("buildingId", ""), 200, "Building ID")}
        if not result["target"]["buildingId"]:
            raise ValueError("Choose a building footprint")
    elif mode == "buildings":
        ids = target.get("buildingIds")
        if not isinstance(ids, list) or not ids or len(ids) > 500 or not all(isinstance(i, str) and 0 < len(i) <= 200 for i in ids):
            raise ValueError("Give between 1 and 500 building IDs")
        result["target"] = {"mode": mode, "buildingIds": ids}
    elif mode == "circle":
        lat, lon = float(target.get("lat")), float(target.get("lon"))
        radius = float(target.get("radiusM"))
        if not (-90 <= lat <= 90 and -180 <= lon <= 180 and 1 <= radius <= 5000):
            raise ValueError("A circle needs a position and a radius of 1 to 5000 m")
        result["target"] = {"mode": mode, "lat": lat, "lon": lon, "radiusM": radius}
    elif mode == "area":
        ring = target.get("polygon")
        if not isinstance(ring, list) or len(ring) < 6 or len(ring) % 2 or not all(isinstance(v, (int, float)) for v in ring):
            raise ValueError("An area is a flat latitude, longitude list with at least 3 points")
        result["target"] = {"mode": mode, "polygon": [float(v) for v in ring]}
        share = item.get("target", {}).get("share")
        if share is not None:
            if not isinstance(share, (int, float)) or not 0 < share <= 1:
                raise ValueError("Share must be above 0 and at most 1")
            result["target"]["share"] = float(share)
    else:
        raise ValueError("Target mode must be building, buildings, circle or area")
    if item.get("reviewer"):
        result["reviewer"] = _text(item["reviewer"], 80, "Reviewer")
    result["updatedAt"] = _now()
    return result


def save_source(data_root, cache_root, area_id, item):
    with LOCK:
        catalog = read_catalog(data_root, cache_root, area_id)
        source = validate_source(item)
        state = _folder(cache_root, area_id) / "sources.json"
        saved = {s["id"]: s for s in _read_json(state, [])}
        if len(saved) >= 2000 and source["id"] not in saved:
            raise ValueError("This catalog has reached 2,000 user sources")
        saved[source["id"]] = source
        _write_json(state, list(saved.values()))
        return source


def save_assessment(data_root, cache_root, area_id, item):
    with LOCK:
        catalog = read_catalog(data_root, cache_root, area_id)
        known = {a["id"]: a for a in catalog["assessments"]}
        merged = copy.deepcopy(known.get(item.get("id"), {})) if isinstance(item, dict) else {}
        merged.update(item)
        assessment = validate_assessment(merged, catalog)
        if "reviewer" not in assessment:
            assessment["reviewer"] = "user"
        state = _folder(cache_root, area_id) / "assessments.json"
        saved = {a["id"]: a for a in _read_json(state, [])}
        if len(saved) >= 20000 and assessment["id"] not in saved:
            raise ValueError("This catalog has reached 20,000 user assessments")
        saved[assessment["id"]] = assessment
        _write_json(state, list(saved.values()))
        return assessment


# ------------------------------------------------------------------ OSM tags

def from_tags(tags):
    """Evidence stated by OpenStreetMap tags, or None. Only what the tags say: a ruin is destroyed."""
    if not isinstance(tags, dict):
        return None
    building = str(tags.get("building", "")).lower()
    if building == "ruins" or str(tags.get("ruins", "")).lower() == "yes" or "ruins:building" in tags:
        return {"state": "destroyed", "basis": "mapped", "confidence": "medium",
                "note": "OpenStreetMap maps this as a ruin (building=ruins)."}
    for key in tags:
        if key.startswith(("destroyed:", "demolished:")):
            return {"state": "destroyed", "basis": "mapped", "confidence": "medium",
                    "note": f"OpenStreetMap carries the lifecycle tag {key}."}
    damage = str(tags.get("damage", "")).lower()
    if damage in ("yes", "damaged", "war"):
        return {"state": "damaged", "basis": "mapped", "confidence": "low", "note": "OpenStreetMap tags damage on this building."}
    return None


# ------------------------------------------------------------------ resolving

def _stable_fraction(text):
    """A number in [0, 1) that depends only on the text, so a share of an area is always the same buildings."""
    return int(hashlib.sha1(text.encode("utf-8")).hexdigest()[:8], 16) / 0x100000000


def _ring_xy(flat, proj):
    return [proj.forward(flat[i], flat[i + 1]) for i in range(0, len(flat) - 1, 2)]


def collect(export, catalog, rasters=None):
    """Evidence per building ID: {building id: [evidence dict, ...]}, strongest kinds first is not assumed here."""
    buildings = export.get("buildings", [])
    by_id = {b["id"]: b for b in buildings}
    sources = {s["id"]: s for s in catalog["sources"]}
    evidence = {}

    def add(building_id, entry):
        evidence.setdefault(building_id, []).append(entry)

    for b in buildings:
        # the extractor stores what the OSM tags say as mappedCondition; older exports only have buildingTag
        mapped = b.get("mappedCondition") or (from_tags({"building": "ruins"}) if b.get("buildingTag") == "ruins" else None)
        if mapped:
            add(b["id"], {**mapped, "asOf": None, "sourceId": "osm", "assessmentId": None})

    origin = export.get("origin") or {}
    proj = geo.EnuProjection(origin["lat"], origin["lon"]) if origin else None
    spatial = None
    for a in catalog["assessments"]:
        if a.get("status") == "rejected" or a.get("sourceId") not in sources:
            continue
        base = {"state": a["state"], "basis": a["basis"], "confidence": a["confidence"], "asOf": a["asOf"],
                "sourceId": a["sourceId"], "assessmentId": a["id"], "note": a.get("note", "")}
        if a.get("status") == "candidate":
            continue  # a candidate has not been confirmed by a person
        target = a["target"]
        if target["mode"] == "building":
            if target["buildingId"] in by_id:
                add(target["buildingId"], base)
        elif target["mode"] == "buildings":
            for building_id in target["buildingIds"]:
                if building_id in by_id:
                    add(building_id, base)
        elif proj is not None:
            if spatial is None:
                spatial = geo.GridIndex(50.0)
                for b in buildings:
                    spatial.insert(b, (b["x"] - 25, b["z"] - 25, b["x"] + 25, b["z"] + 25))
            if target["mode"] == "circle":
                cx, cz = proj.forward(target["lat"], target["lon"])
                r = target["radiusM"]
                candidates = spatial.query_box((cx - r, cz - r, cx + r, cz + r))
                inside = [b for b in candidates if math.hypot(b["x"] - cx, b["z"] - cz) <= r]
            else:
                ring = _ring_xy(target["polygon"], proj)
                box = geo.bbox(ring)
                inside = [b for b in spatial.query_box(box) if geo.point_in_ring(b["x"], b["z"], ring)]
            share = target.get("share")
            seen = set()
            for b in inside:
                if b["id"] in seen:
                    continue
                seen.add(b["id"])
                if share is not None and _stable_fraction(a["id"] + "|" + b["id"]) >= share:
                    continue
                entry = dict(base)
                if share is not None:
                    entry["basis"] = "estimated"
                    entry["confidence"] = "low"
                add(b["id"], entry)
    for raster in rasters or []:
        for building_id, entry in raster.estimates(export).items():
            add(building_id, entry)
    return evidence


def resolve_one(entries):
    """The condition of one building from its evidence, or None.

    Explicit evidence (observed, reported, mapped) outranks an estimate. Damage does not heal during a war,
    so among explicit evidence the worst state wins, with the latest date and the highest confidence of the
    entries that state it. Without any explicit evidence the worst estimate stands.
    """
    if not entries:
        return None
    explicit = [e for e in entries if e["basis"] in EXPLICIT]
    pool = explicit or [e for e in entries if e["basis"] == "estimated"]
    if not pool:
        return None
    worst = max(SEVERITY[e["state"]] for e in pool)
    top = [e for e in pool if SEVERITY[e["state"]] == worst]
    dates = [e["asOf"] for e in top if e.get("asOf")]
    best = max(top, key=lambda e: (CONFIDENCE[e["confidence"]], e.get("asOf") or ""))
    result = {"state": STATES[worst], "basis": best["basis"], "confidence": best["confidence"],
              "sources": sorted({e["sourceId"] for e in top})}
    if dates:
        result["asOf"] = max(dates)
    notes = [e["note"] for e in top if e.get("note")]
    if notes:
        result["note"] = notes[0][:300]
    scores = [e["score"] for e in top if "score" in e]
    if scores:
        result["score"] = round(max(scores), 3)
    return result


def enrich(export, env, catalog, rasters=None):
    """Writes `condition` onto the buildings that have evidence; returns counts. Safe to run again.

    Footprints, heights and floors are never changed: condition is an extra field that the renderer
    may use to draw damage. Positions come from building IDs or from areas the evidence names.
    """
    for b in export.get("buildings", []):
        b.pop("condition", None)
    evidence = collect(export, catalog, rasters)
    counts = {state: 0 for state in STATES}
    bases = {basis: 0 for basis in BASES}
    for b in export.get("buildings", []):
        resolved = resolve_one(evidence.get(b["id"]))
        if resolved:
            b["condition"] = resolved
            counts[resolved["state"]] += 1
            bases[resolved["basis"]] += 1
    total = len(export.get("buildings", []))
    known = sum(counts.values())
    stats = {"buildings": total, "withCondition": known, "unknown": total - known, "states": counts, "bases": bases}
    stats["message"] = (f"{known} of {total} buildings have a condition "
                        f"({counts['destroyed']} destroyed, {counts['heavily_damaged']} heavily damaged, "
                        f"{counts['damaged']} damaged, {counts['intact']} intact); {total - known} unknown.")
    layer = {"file": "condition.json", "areaId": catalog["area"]["id"], "summary": stats,
             "note": "Condition is evidence-based and dated. Estimates from satellite radar are low confidence; unknown means no evidence."}
    export["conditionLayer"] = layer
    if env is not None:
        env["conditionLayer"] = copy.deepcopy(layer)
        env.setdefault("summary", {})["conditionLayer"] = stats
    return stats


def load_rasters(catalog):
    """The radar estimates listed in a catalog, ready to use; a missing or damaged file is skipped."""
    from . import sar
    result = []
    for entry in catalog.get("rasters", []):
        if entry.get("enabled") is False:
            continue
        try:
            raster = sar.SarRaster.load(Path(catalog["folder"]) / entry.get("folder", "sar"), entry["id"])
        except (OSError, ValueError, KeyError):
            continue
        raster.thresholds.update(entry.get("thresholds") or {})
        raster.category_thresholds.update(entry.get("categoryThresholds") or {})
        raster.meta["calibration"] = entry.get("calibration", [])
        result.append(raster)
    return result


def summary(buildings):
    states = {state: 0 for state in STATES}
    for b in buildings:
        condition = b.get("condition")
        if condition and condition.get("state") in states:
            states[condition["state"]] += 1
    return {"withCondition": sum(states.values()), "states": states}


def export_bounds(export):
    """(south, west, north, east) of an export's selection, or None."""
    flat = export.get("area", {}).get("polygon", [])
    if len(flat) < 6:
        return None
    return min(flat[::2]), min(flat[1::2]), max(flat[::2]), max(flat[1::2])


def overlap_share(export, catalog):
    """How much of the export's bounding box the catalog's area covers, 0 to 1."""
    box, bounds = export_bounds(export), catalog.get("area", {}).get("bounds")
    if not box or not bounds:
        return 0.0
    (south, west), (north, east) = bounds
    height = min(box[2], north) - max(box[0], south)
    width = min(box[3], east) - max(box[1], west)
    if height < 0 or width < 0:
        return 0.0
    whole = max(box[2] - box[0], 1e-9) * max(box[3] - box[1], 1e-9)
    return min(1.0, max(height, 1e-9) * max(width, 1e-9) / whole)


def overlaps(export, catalog):
    return overlap_share(export, catalog) > 0


def area_ids(data_root, cache_root):
    """Every area with a catalog: those that come with the app first, then those the app made."""
    found = []
    for root in (data_root, cache_root):
        root = Path(root)
        if root.is_dir():
            for folder in sorted(root.iterdir()):
                if (folder / "catalog.json").is_file() and re.fullmatch(r"[a-z0-9-]{1,60}", folder.name) and folder.name not in found:
                    found.append(folder.name)
    return found


def find_catalog(data_root, cache_root, export):
    """The catalog of the area that covers the export best (an area with a radar estimate before one without), or None."""
    best, best_key = None, None
    for area_id in area_ids(data_root, cache_root):
        try:
            catalog = read_catalog(data_root, cache_root, area_id)
        except (OSError, ValueError, KeyError):
            continue
        share = overlap_share(export, catalog)
        if share <= 0:
            continue
        key = (round(share, 3), bool(catalog.get("rasters")))
        if best_key is None or key > best_key:
            best, best_key = catalog, key
    return best


def area_km2(bounds):
    """Size of a {west, south, east, north} box in square kilometres."""
    middle = math.radians((bounds["south"] + bounds["north"]) / 2)
    return (bounds["north"] - bounds["south"]) * 111.32 * (bounds["east"] - bounds["west"]) * 111.32 * math.cos(middle)


def radar_area(data_root, cache_root, export, name="", create=True):
    """The area whose radar estimate an export uses: one that covers it whole, or a new one made from its bounds.

    A new area is kept in cache_root as auto-<hash of its bounds>, so extracting the same place again finds it.
    create=False only checks that one can be made (None then stands for the new area).
    """
    for area_id in area_ids(data_root, cache_root):
        try:
            catalog = read_catalog(data_root, cache_root, area_id)
        except (OSError, ValueError, KeyError):
            continue
        if overlap_share(export, catalog) >= 0.999 and catalog["area"].get("analysisBounds"):
            return area_id
    box = export_bounds(export)
    if not box:
        raise ValueError("The export has no selection area")
    south, west = round(box[0] - AUTO_MARGIN, 5), round(box[1] - AUTO_MARGIN, 5)
    north, east = round(box[2] + AUTO_MARGIN, 5), round(box[3] + AUTO_MARGIN, 5)
    analysis = {"west": west, "south": south, "east": east, "north": north}
    if area_km2(analysis) > MAX_RADAR_KM2:
        raise ValueError(f"The area is {area_km2(analysis):.0f} km2: a radar estimate takes at most {MAX_RADAR_KM2:.0f} km2. "
                         "Extract it in parts.")
    if not create:
        return None
    area_id = "auto-" + hashlib.sha1(f"{south},{west},{north},{east}".encode()).hexdigest()[:10]
    label = re.sub(r"[_-]+", " ", name or "").strip()
    if label.lower() in ("", "buildings"):  # the app's name for an export nobody named
        label = f"Area at {(south + north) / 2:.4f} N, {(west + east) / 2:.4f} E"
    with LOCK:
        folder = _folder(cache_root, area_id)
        if not (folder / "catalog.json").is_file():
            _write_json(folder / "catalog.json", {
                "format": FORMAT, "version": VERSION,
                "area": {"id": area_id, "name": label[:80], "center": [(south + north) / 2, (west + east) / 2],
                         "bounds": [[south, west], [north, east]], "analysisBounds": analysis, "newsQuery": label[:80],
                         "createdAt": _now()},
                "sources": [], "assessments": [], "rasters": [],
                "notes": ["Made by the app for a radar estimate of an export. The radar score lines are the general "
                          "ones: no published figures calibrate them for this area."]})
    return area_id


def radar_is_fresh(catalog, today=None, days=RADAR_FRESH_DAYS):
    """True when the area has a radar estimate made in the last `days` days."""
    today = dt.date.fromisoformat(today) if today else dt.date.today()
    for entry in catalog.get("rasters", []):
        try:
            meta = json.loads((Path(catalog["folder"]) / entry.get("folder", "sar") / f"{entry['id']}.json").read_text(encoding="utf-8"))
            if (today - dt.date.fromisoformat(meta["createdAt"])).days <= days:
                return True
        except (OSError, ValueError, KeyError):
            continue
    return False


def estimate_radar(data_root, cache_root, cog_cache, export, name, log=print, progress=lambda f, m=None: None,
                   is_cancelled=lambda: False, orbit=None, reuse=True):
    """A radar estimate of the damage in the area of an export; returns the area's catalog.

    The area is one that covers the export whole (with the app or made earlier), or a new one made from the
    export's bounds. reuse: keep an estimate made in the last days instead of reading the scenes again.
    """
    from . import sar
    area_id = radar_area(data_root, cache_root, export, name)
    catalog = read_catalog(data_root, cache_root, area_id)
    if reuse and radar_is_fresh(catalog):
        log(f"Radar: the estimate of {catalog['area']['name']} is recent, it is used as it is")
        return catalog
    b = catalog["area"]["analysisBounds"]
    log(f"Radar: estimating the damage of {catalog['area']['name']} ({area_km2(b):.0f} km2) from Sentinel-1")
    result = sar.analyse((b["west"], b["south"], b["east"], b["north"]), cog_cache, relative_orbit=orbit,
                         log=log, progress=progress, is_cancelled=is_cancelled)
    store_raster(data_root, cache_root, area_id, result)
    log(f"Radar: saved estimate {result.id} (newest scene {result.meta['monitoring'].get('latest')})")
    return read_catalog(data_root, cache_root, area_id)


def store_raster(data_root, cache_root, area_id, result):
    """Saves a radar estimate (sar.SarRaster) with its area and lists it, with its source, in the catalog."""
    with LOCK:
        folder = catalog_folder(data_root, cache_root, area_id)
        result.save(folder / "sar")
        path = folder / "catalog.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        data["rasters"] = [r for r in data.get("rasters", []) if r["id"] != result.id] + [
            {"id": result.id, "folder": "sar", "kind": "sar_change"}]
        data["sources"] = [s for s in data.get("sources", []) if s["id"] != result.id] + [result.source()]
        _write_json(path, data)


# ------------------------------------------------------------------ package files

def package_files(catalog, export, rasters=None):
    """Files that travel with an export: the evidence ledger, its credits and the raster products used."""
    used = {s for b in export.get("buildings", []) for s in (b.get("condition") or {}).get("sources", [])}
    # a report that sets how many buildings the radar marks is credited too
    used |= {c["sourceId"] for r in rasters or [] for c in r.meta.get("calibration", []) if c.get("sourceId")}
    sources = [s for s in catalog["sources"] if s["id"] in used]
    sources.append({"id": "osm", "kind": "osm", "title": "OpenStreetMap building tags", "publisher": "OpenStreetMap contributors",
                    "url": "https://www.openstreetmap.org/copyright",
                    "license": {"name": "ODbL 1.0", "url": "https://opendatacommons.org/licenses/odbl/1-0/"}})
    ledger = {"format": FORMAT, "version": VERSION, "area": catalog["area"], "generatedAt": _now(),
              "summary": (export.get("conditionLayer") or {}).get("summary"), "sources": sources,
              "assessments": [a for a in catalog["assessments"] if a.get("status") == "confirmed" and a["sourceId"] in used],
              "rasters": [r.describe() for r in rasters or []]}
    credits = ["Building condition evidence", "",
               "Condition states are evidence-based and dated; each building lists its sources in buildings.json.",
               "Estimates from satellite radar are low confidence. Buildings without evidence are 'unknown'.", ""]
    for s in sources:
        credits.append(f"{s['title']} - {s.get('publisher', '')} {s.get('publishedAt') or ''}".strip())
        if s.get("url"):
            credits.append("  " + s["url"])
        if s.get("license"):
            credits.append(f"  License: {s['license']['name']}")
    files = {"condition.json": json.dumps(ledger, ensure_ascii=False, indent=1).encode("utf-8"),
             "CONDITION-CREDITS.txt": "\n".join(credits).encode("utf-8")}
    for raster in rasters or []:
        files.update(raster.files())
    return files
