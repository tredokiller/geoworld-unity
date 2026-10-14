"""Dated photo references and explicitly placed visual observations.

A reference geotag is never an object's position. Only a reviewed building ID or
a separate map placement can affect a generated scene. No imagery service or AI
request is made while opening this local library.
"""
import copy
import base64
import binascii
import datetime as dt
import hashlib
import io
import json
import math
import re
import threading
import struct
import uuid
import zipfile
import zlib
from pathlib import Path
from urllib.parse import urlsplit

from . import geo, roofs

FORMAT = "building-extractor.street-references"
LOCK = threading.RLock()
POINT_TYPES = {"bench", "street_lamp", "waste_basket", "bollard", "shelter", "bus_stop",
               "bicycle_parking", "power_pole", "post_box", "gate"}
KINDS = {"building", "prop", "surface", "vegetation"}
TEXT_ATTRIBUTES = {"windowStyle", "style", "roofNote", "paving", "vegetation", "material"}
COLOR_ATTRIBUTES = {"wallColor", "roofColor", "colour"}


def _now():
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def _text(value, limit, name):
    if not isinstance(value, str) or len(value) > limit or any(ord(c) < 32 and c not in "\n\t" for c in value):
        raise ValueError(f"{name} must be text with at most {limit} characters")
    return value.strip()


def _number(value, low, high, name):
    if isinstance(value, bool) or not isinstance(value, (float, int)) or not math.isfinite(value) or not low <= value <= high:
        raise ValueError(f"{name} must be a number between {low} and {high}")
    return value


def _folder(root, area_id):
    if not isinstance(area_id, str) or not re.fullmatch(r"[a-z0-9-]{1,60}", area_id):
        raise ValueError("Invalid reference area")
    result = Path(root) / area_id
    if result.resolve().parent != Path(root).resolve():
        raise ValueError("Invalid reference area")
    return result


def read_catalog(data_root, cache_root, area_id="lyman", public=True):
    folder = _folder(data_root, area_id)
    data = json.loads((folder / "catalog.json").read_text(encoding="utf-8"))
    if data.get("format") != FORMAT or data.get("version") != 1:
        raise ValueError("Unsupported photo library")
    with LOCK:
        state = _folder(cache_root, area_id) / "observations.json"
        overrides = json.loads(state.read_text(encoding="utf-8")) if state.exists() else []
        photos_path = state.parent / "photos.json"
        if photos_path.exists():
            data["photos"].extend(json.loads(photos_path.read_text(encoding="utf-8")))
    observations = {o["id"]: o for o in data.get("observations", [])}
    observations.update({o["id"]: o for o in overrides})
    data["observations"] = list(observations.values())
    for o in data["observations"]:
        o["status"] = "linked" if o.get("placement") else "reference"
    if public:
        for photo in data["photos"]:
            photo["imageUrl"] = f"/reference-media/{area_id}/{photo['localFile']}"
        data["downloadUrl"] = f"/api/references/{area_id}/download"
    return data


def media_file(data_root, area_id, name, cache_root=None):
    folder = _folder(data_root, area_id)
    data = json.loads((folder / "catalog.json").read_text(encoding="utf-8"))
    allowed = {p["localFile"] for p in data["photos"]}
    path = folder / name
    if name not in allowed and cache_root is not None:
        state = _folder(cache_root, area_id)
        manifest = state / "photos.json"
        uploaded = json.loads(manifest.read_text(encoding="utf-8")) if manifest.exists() else []
        allowed = {p["localFile"] for p in uploaded}
        folder = state / "media"
        path = folder / name
    if name not in allowed or path.resolve().parent != folder.resolve() or not path.is_file():
        raise FileNotFoundError("Photo not found")
    return path


def _url(value, name):
    value = _text(value, 2000, name)
    if value:
        parsed = urlsplit(value)
        if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password:
            raise ValueError(f"{name} must be an HTTP or HTTPS link")
    return value


def image_dimensions(data, mime):
    """Read bounded PNG/JPEG dimensions without decoding untrusted pixels."""
    if mime == "image/png":
        if len(data) < 33 or data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR" or data[8:12] != b"\x00\x00\x00\x0d":
            raise ValueError("Invalid PNG image")
        width, height = struct.unpack_from(">II", data, 16)
        offset, image_data, ended = 8, False, False
        while offset + 12 <= len(data):
            length = struct.unpack_from(">I", data, offset)[0]
            end = offset + length + 12
            if end > len(data):
                raise ValueError("Truncated PNG image")
            kind = data[offset + 4:offset + 8]
            crc = struct.unpack_from(">I", data, end - 4)[0]
            if zlib.crc32(data[offset + 4:end - 4]) & 0xffffffff != crc:
                raise ValueError("Invalid PNG checksum")
            image_data |= kind == b"IDAT" and length > 0
            if kind == b"IEND":
                ended = length == 0 and end == len(data)
                break
            offset = end
        if not image_data or not ended:
            raise ValueError("Incomplete PNG image")
    else:
        if not data.startswith(b"\xff\xd8") or not data.endswith(b"\xff\xd9"):
            raise ValueError("Invalid JPEG image")
        width = height = 0
        offset = 2
        while offset < len(data):
            if data[offset] != 255:
                break
            while offset < len(data) and data[offset] == 255:
                offset += 1
            if offset >= len(data):
                break
            marker = data[offset]
            offset += 1
            if marker in (0xD8, 0xD9, 0x01) or 0xD0 <= marker <= 0xD7:
                continue
            if marker == 0xDA or offset + 2 > len(data):
                break
            length = struct.unpack_from(">H", data, offset)[0]
            if length < 2 or offset + length > len(data):
                raise ValueError("Truncated JPEG image")
            if marker in (0xC0, 0xC1, 0xC2):
                if length < 7:
                    raise ValueError("Invalid JPEG dimensions")
                height, width = struct.unpack_from(">HH", data, offset + 3)
                break
            offset += length
    if not (1 <= width <= 10000 and 1 <= height <= 10000 and width * height <= 25000000):
        raise ValueError("Image must be at most 10,000 pixels per side and 25 megapixels")
    return width, height


def save_photo(data_root, cache_root, area_id, item):
    """Import a local north-up image, with user-declared rights and known bounds."""
    if not isinstance(item, dict) or item.get("kind") != "overhead_image":
        raise ValueError("Choose a north-up overhead image")
    if item.get("reuseAllowed") is not True:
        raise ValueError("Confirm that you own this image or its license permits derived data")
    if item.get("crs") != "EPSG:3857":
        raise ValueError("Use a north-up Web Mercator (EPSG:3857) image export")
    catalog = read_catalog(data_root, cache_root, area_id, public=False)
    bounds = item.get("bounds")
    if not isinstance(bounds, list) or len(bounds) != 2 or any(not isinstance(p, list) or len(p) != 2 for p in bounds):
        raise ValueError("Image bounds need south, west, north and east coordinates")
    south, north = [_number(p[0], -85, 85, "Latitude") for p in bounds]
    west, east = [_number(p[1], -180, 180, "Longitude") for p in bounds]
    if south >= north or west >= east or north - south > 0.2 or east - west > 0.3:
        raise ValueError("Use an ordered local image extent (at most 0.2° latitude and 0.3° longitude)")
    (s, w), (n, e) = catalog["area"]["bounds"]
    if north <= s or south >= n or east <= w or west >= e:
        raise ValueError("The image must overlap this reference area")
    title = _text(item.get("title", ""), 200, "Title")
    attribution = _text(item.get("attribution", ""), 300, "Attribution")
    license_info = item.get("license", {})
    if not isinstance(license_info, dict):
        raise ValueError("Add a license name")
    license_name = _text(license_info.get("name", ""), 100, "License")
    if not title or not attribution or not license_name:
        raise ValueError("Title, attribution and license name are required")
    captured = item.get("capturedAt")
    if captured is not None and not isinstance(captured, str):
        raise ValueError("Capture date must be YYYY-MM-DD or empty")
    captured = captured or None
    if captured is not None:
        if not isinstance(captured, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", captured):
            raise ValueError("Capture date must be YYYY-MM-DD or empty")
        dt.date.fromisoformat(captured)
    data_url = item.get("dataUrl", "")
    if not isinstance(data_url, str) or len(data_url) > 17 * 1024 * 1024:
        raise ValueError("Image must be at most 12 MB")
    match = re.fullmatch(r"data:(image/(?:png|jpeg));base64,([A-Za-z0-9+/=\r\n]+)", data_url)
    if not match:
        raise ValueError("Choose a PNG or JPEG image")
    try:
        payload = base64.b64decode(match[2], validate=True)
    except (ValueError, binascii.Error):
        raise ValueError("Invalid image encoding") from None
    if len(payload) > 12 * 1024 * 1024:
        raise ValueError("Image must be at most 12 MB")
    width, height = image_dimensions(payload, match[1])
    photo_id = "uploaded-" + uuid.uuid4().hex
    file_name = photo_id + (".png" if match[1] == "image/png" else ".jpg")
    photo = {"id": photo_id, "title": title, "kind": "overhead_image", "provider": "Local image",
             "localFile": file_name, "bounds": [[south, west], [north, east]], "crs": "EPSG:3857",
             "position": {"lat": (south + north) / 2, "lon": (west + east) / 2},
             "positionMeaning": "image_extent_center", "capturedAt": captured, "uploadedAt": _now(),
             "sourceUrl": _url(item.get("sourceUrl", ""), "Source URL"), "attribution": attribution,
             "license": {"name": license_name, "url": _url(license_info.get("url", ""), "License URL")},
             "reuseAllowed": True, "rightsDeclaration": "User declares ownership or permission to derive data",
             "width": width, "height": height, "bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest(),
             "notes": ["User-supplied north-up extent; check alignment against footprints before sampling.",
                       "Colour suggestions are approximate appearance observations, not roof material or shape measurements."]}
    with LOCK:
        folder = _folder(cache_root, area_id)
        manifest = folder / "photos.json"
        photos = json.loads(manifest.read_text(encoding="utf-8")) if manifest.exists() else []
        if len(photos) >= 100:
            raise ValueError("This library has reached 100 imported images")
        media = folder / "media"
        media.mkdir(parents=True, exist_ok=True)
        path = media / file_name
        temporary = folder / (uuid.uuid4().hex + ".tmp")
        try:
            path.write_bytes(payload)
            temporary.write_text(json.dumps(photos + [photo], ensure_ascii=False, indent=2), encoding="utf-8")
            temporary.replace(manifest)
        except Exception:
            path.unlink(missing_ok=True)
            raise
        finally:
            temporary.unlink(missing_ok=True)
    return {**photo, "imageUrl": f"/reference-media/{area_id}/{file_name}"}


def validate_observation(item, catalog):
    """Normalize user input; locations, measurements and styles stay explicit."""
    if not isinstance(item, dict):
        raise ValueError("An observation must be an object")
    result = {}
    result["id"] = _text(item.get("id", ""), 100, "ID")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", result["id"]):
        raise ValueError("Invalid observation ID")
    result["photoId"] = item.get("photoId")
    if result["photoId"] not in {p["id"] for p in catalog["photos"]}:
        raise ValueError("Choose a photo in this library")
    result["kind"] = item.get("kind")
    if result["kind"] not in KINDS:
        raise ValueError("Choose building, prop, surface or vegetation")
    result["type"] = _text(item.get("type", ""), 60, "Type")
    if not re.fullmatch(r"[a-z0-9_]{1,60}", result["type"]):
        raise ValueError("Type must use lowercase letters, numbers or underscores")
    result["description"] = _text(item.get("description", ""), 2000, "Description")
    if not result["description"]:
        raise ValueError("Describe what is visible in the photo")
    result["confidence"] = item.get("confidence", "medium")
    if result["confidence"] not in {"low", "medium", "high"}:
        raise ValueError("Choose a confidence level")
    result["evidence"] = item.get("evidence", "visible")
    if result["evidence"] not in {"visible", "inferred"}:
        raise ValueError("Evidence must be visible or inferred")
    attributes = item.get("attributes", {})
    if not isinstance(attributes, dict):
        raise ValueError("Attributes must be an object")
    result["attributes"] = {}
    for key, value in attributes.items():
        if key in TEXT_ATTRIBUTES:
            value = _text(value, 500, key)
        elif key in COLOR_ATTRIBUTES:
            if not isinstance(value, str) or not re.fullmatch(r"#[0-9a-fA-F]{6}", value):
                raise ValueError(f"{key} must be a six-digit hex colour")
        elif key == "roofShape":
            if value not in {"flat", "gabled", "hipped"}:
                raise ValueError("Roof shape must be flat, gabled or hipped; leave it empty if unknown")
        elif key == "wallMaterial":
            if value not in {"brick", "plaster", "concrete", "stone", "wood", "metal", "mixed", "unknown"}:
                raise ValueError("Unsupported wall material")
        elif key == "levels":
            value = _number(value, 1, 100, key)
            if value != int(value):
                raise ValueError("Observed floors must be a whole number")
            value = int(value)
        elif key in {"height", "width"}:
            value = _number(value, 0.1, 50, key)
        else:
            raise ValueError(f"Unsupported attribute: {key}")
        result["attributes"][key] = value
    placement = item.get("placement")
    result["placement"] = None
    if placement is not None:
        if not isinstance(placement, dict):
            raise ValueError("Placement must be an object")
        mode = placement.get("mode")
        if mode == "building" and result["kind"] == "building":
            building_id = _text(placement.get("buildingId", ""), 200, "Building ID")
            if not building_id:
                raise ValueError("Choose a building footprint")
            result["placement"] = {"mode": mode, "buildingId": building_id}
        elif mode == "point" and result["kind"] == "prop" and result["type"] in POINT_TYPES:
            lat = _number(placement.get("lat"), -90, 90, "Latitude")
            lon = _number(placement.get("lon"), -180, 180, "Longitude")
            (south, west), (north, east) = catalog["area"]["bounds"]
            if not south <= lat <= north or not west <= lon <= east:
                raise ValueError("Place the prop inside the reference area")
            result["placement"] = {"mode": mode, "lat": lat, "lon": lon}
            if placement.get("rotation") is not None:
                result["placement"]["rotation"] = _number(placement["rotation"], 0, 360, "Rotation") % 360
        else:
            raise ValueError("This observation is a style reference; only building links and supported point props can be placed")
    result["source"] = "visual_review"
    result["reviewer"] = _text(item.get("reviewer", "user"), 80, "Reviewer")
    result["reviewedAt"] = item.get("reviewedAt") or _now()
    result["updatedAt"] = _now()
    return result


def save_observation(data_root, cache_root, area_id, patch):
    if not isinstance(patch, dict):
        raise ValueError("An observation must be an object")
    with LOCK:
        catalog = read_catalog(data_root, cache_root, area_id, public=False)
        existing = {o["id"]: o for o in catalog["observations"]}
        item_id = patch.get("id")
        if item_id and item_id not in existing:
            raise ValueError("Unknown observation")
        item = copy.deepcopy(existing.get(item_id, {}))
        item.update(patch)
        item["id"] = item_id or "obs-" + uuid.uuid4().hex[:16]
        item["reviewer"] = "user"
        item = validate_observation(item, catalog)
        folder = _folder(cache_root, area_id)
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / "observations.json"
        overrides = json.loads(path.read_text(encoding="utf-8")) if path.exists() else []
        overrides = {o["id"]: o for o in overrides}
        if len(overrides) >= 1000 and item["id"] not in overrides:
            raise ValueError("This library has reached 1,000 saved observations")
        overrides[item["id"]] = item
        temporary = folder / (uuid.uuid4().hex + ".tmp")
        try:
            temporary.write_text(json.dumps(list(overrides.values()), ensure_ascii=False, indent=2), encoding="utf-8")
            temporary.replace(path)
        finally:
            if temporary.exists():
                temporary.unlink()
        return item


def overlaps(export, catalog):
    flat = export.get("area", {}).get("polygon", [])
    if len(flat) < 6:
        return False
    (south, west), (north, east) = catalog["area"]["bounds"]
    return not (max(flat[::2]) < south or min(flat[::2]) > north or max(flat[1::2]) < west or min(flat[1::2]) > east)


def enrich(export, env, catalog):
    """Apply only explicit links inside this export; preserve geometry and height.

    Returns counts. The caller decides whether to copy first. This is idempotent
    for photo props, and rebuilding a fresh enriched copy updates their positions.
    """
    stats = {"buildings": 0, "props": 0, "skipped": 0, "referenceOnly": 0}
    by_id = {b["id"]: b for b in export.get("buildings", [])}
    photos = {p["id"]: p for p in catalog["photos"]}
    linked_buildings = set()
    proj = geo.EnuProjection(export["origin"]["lat"], export["origin"]["lon"])
    flat = export.get("area", {}).get("polygon", [])
    ring = [proj.forward(flat[i], flat[i + 1]) for i in range(0, len(flat) - 1, 2)]
    # Refresh previous applications from this library, leaving other sources intact.
    reset = set()
    for b in by_id.values():
        if (b.get("visualDetails") or {}).get("libraryId") == catalog["area"]["id"]:
            b.pop("visualDetails", None)
            reset.add(b["id"])
    if env is not None:
        env["props"] = [p for p in env.get("props", []) if p.get("referenceLibrary") != catalog["area"]["id"]]
    for obs in catalog["observations"]:
        placement = obs.get("placement")
        if not placement:
            stats["referenceOnly"] += 1
            continue
        photo = photos[obs["photoId"]]
        evidence = {"source": "overhead_image" if photo.get("kind") == "overhead_image" else "photo_reference", "photoId": photo["id"], "observationId": obs["id"],
                    "sourceUrl": photo["sourceUrl"], "capturedAt": photo.get("capturedAt"),
                    "confidence": obs["confidence"], "description": obs["description"],
                    "evidence": obs["evidence"], "license": photo["license"], "attribution": photo["attribution"]}
        if placement["mode"] == "building":
            building = by_id.get(placement["buildingId"])
            if building is None:
                stats["skipped"] += 1
                continue
            existing_details = building.get("visualDetails")
            if existing_details and existing_details.get("libraryId") != catalog["area"]["id"]:
                stats["skipped"] += 1
                continue
            detail = building.setdefault("visualDetails", {"libraryId": catalog["area"]["id"], "references": []})
            detail.update({k: v for k, v in obs["attributes"].items() if k in
                           {"wallColor", "roofColor", "roofShape", "wallMaterial", "levels", "windowStyle", "style", "roofNote"}})
            detail.update(evidence)
            detail["references"].append(evidence)
            linked_buildings.add(building["id"])
        elif env is not None and placement["mode"] == "point":
            x, z = proj.forward(placement["lat"], placement["lon"])
            if len(ring) < 3 or not geo.point_in_ring(x, z, ring):
                stats["skipped"] += 1
                continue
            attrs = obs["attributes"]
            prop = {"id": "photo:" + obs["id"], "type": obs["type"], "name": obs["description"][:100],
                    "x": round(x, 2), "z": round(z, 2), "placementSource": "manual_photo",
                    "referenceLibrary": catalog["area"]["id"], "height": attrs.get("height", 0),
                    "width": attrs.get("width", 0), "material": attrs.get("material", ""),
                    "colour": attrs.get("colour", ""), "rotationSource": "unknown", **evidence}
            if "rotation" in placement:
                prop.update(rotation=placement["rotation"], rotationSource="manual")
            env["props"].append(prop)
            stats["props"] += 1
        else:
            stats["skipped"] += 1
    # A reviewed roof replaces what was mapped or estimated; a withdrawn review gives the old value back.
    estimating = bool((export.get("roofInfo") or {}).get("estimated"))
    for building_id in reset | linked_buildings:
        roofs.refresh(by_id[building_id], estimating)
    if "roofInfo" in export:
        export["roofInfo"]["summary"] = roofs.summary(export.get("buildings", []))
    stats["buildings"] = len(linked_buildings)
    stats["message"] = (f"{stats['buildings']} buildings styled, {stats['props']} photo props placed; "
                        f"{stats['referenceOnly']} observations remain references, {stats['skipped']} links outside this export or unsupported.")
    export["streetReferences"] = {"file": "street-references.json", "areaId": catalog["area"]["id"], "summary": stats,
                                  "note": "Historical visual references; object positions require explicit map links. Capture dates may be unknown."}
    if env is not None:
        env["streetReferences"] = copy.deepcopy(export["streetReferences"])
        env.setdefault("summary", {})["props"] = len(env.get("props", []))
        env["summary"]["photoReferences"] = stats
    return stats


def package_files(data_root, catalog, cache_root=None):
    data = copy.deepcopy(catalog)
    data.pop("downloadUrl", None)
    files = {}
    credits = ["Lyman visual references", "", "Individual image licenses and attribution are listed below; terms vary by source.",
               "Downloaded display copies were resized where noted. No current-condition or camera-position claim is made.",
               "Visual observations: AI-assisted visual review, not measured object coordinates.",
               "Only explicit map placements/building links affect the generated scene.", ""]
    for photo in data["photos"]:
        file_name = "reference-" + photo["localFile"]
        path = media_file(data_root, data["area"]["id"], photo["localFile"], cache_root)
        files[file_name] = path.read_bytes()
        photo.pop("imageUrl", None)
        photo["imageFile"] = file_name
        credits.extend([photo["title"], f"File: {file_name}", f"Author: {photo['attribution']}",
                        f"Source: {photo['sourceUrl']}", f"License: {photo['license']['name']} — {photo['license']['url']}",
                        f"Display copy: {photo['width']} x {photo['height']} pixels", ""])
    files["street-references.json"] = json.dumps(data, ensure_ascii=False, indent=2).encode("utf-8")
    files["STREET-REFERENCE-CREDITS.txt"] = "\n".join(credits).encode("utf-8")
    return files


def zip_bytes(data_root, catalog, cache_root=None):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in package_files(data_root, catalog, cache_root).items():
            z.writestr(name, data)
    return output.getvalue()
