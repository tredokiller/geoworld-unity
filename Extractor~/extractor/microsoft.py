"""Building footprints from Microsoft Global ML Building Footprints.

Microsoft detected these outlines from satellite imagery with a neural
network. The dataset is split into gzip'd GeoJSON-lines files, one per zoom-9
quadkey tile (~50 km wide in Ukraine). Downloaded tiles are cached on disk.
"""
import csv
import gzip
import json
import math
import re
import time
import urllib.request
from pathlib import Path

from . import geo
from .common import Cancelled, USER_AGENT

LINKS_URL = "https://minedbuildings.z5.web.core.windows.net/global-buildings/dataset-links.csv"
LINKS_MAX_AGE = 7 * 24 * 3600
ZOOM = 9

_FIRST_COORD = re.compile(rb'\[\s*\[\s*\[\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)')


def quadkeys_for_bbox(south, west, north, east, zoom=ZOOM):
    def tile(lat, lon):
        n = 1 << zoom
        x = int((lon + 180.0) / 360.0 * n)
        s = math.sin(math.radians(lat))
        y = int((0.5 - math.log((1 + s) / (1 - s)) / (4 * math.pi)) * n)
        return min(max(x, 0), n - 1), min(max(y, 0), n - 1)

    x0, y0 = tile(north, west)
    x1, y1 = tile(south, east)
    keys = []
    for x in range(x0, x1 + 1):
        for y in range(y0, y1 + 1):
            key = ""
            for i in range(zoom, 0, -1):
                digit = 0
                mask = 1 << (i - 1)
                if x & mask:
                    digit += 1
                if y & mask:
                    digit += 2
                key += str(digit)
            keys.append(key)
    return keys


def _load_links(cache_dir, log):
    path = cache_dir / "dataset-links.csv"
    if not path.exists() or time.time() - path.stat().st_mtime > LINKS_MAX_AGE:
        log("Microsoft: downloading dataset index")
        req = urllib.request.Request(LINKS_URL, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(req, timeout=120) as resp:
            data = resp.read()
        path.write_bytes(data)
    links = {}
    with path.open(newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            links.setdefault(row["QuadKey"], []).append(row)
    return links


def _download_tile(url, dest, log, progress, is_cancelled):
    tmp = dest.with_suffix(".part")
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=120) as resp, tmp.open("wb") as out:
        total = int(resp.headers.get("Content-Length") or 0)
        done = 0
        while True:
            if is_cancelled():
                out.close()
                tmp.unlink(missing_ok=True)
                raise Cancelled()
            chunk = resp.read(1 << 18)
            if not chunk:
                break
            out.write(chunk)
            done += len(chunk)
            if total:
                progress(None, f"Microsoft: downloading {dest.name} {done / 1e6:.1f}/{total / 1e6:.1f} MB")
    tmp.replace(dest)


def fetch(ring_latlon, cache_dir, log, progress, is_cancelled):
    """Returns raw buildings whose vertex-average centre lies inside ring_latlon."""
    cache_dir = Path(cache_dir) / "microsoft"
    cache_dir.mkdir(parents=True, exist_ok=True)

    lats = [p[0] for p in ring_latlon]
    lons = [p[1] for p in ring_latlon]
    south, north, west, east = min(lats), max(lats), min(lons), max(lons)
    pad = 0.002
    # Selection polygon in (lon, lat) for point-in-polygon tests.
    sel = [(lon, lat) for lat, lon in ring_latlon]

    links = _load_links(cache_dir, log)
    keys = quadkeys_for_bbox(south, west, north, east)
    rows = [r for k in keys for r in links.get(k, [])]
    if not rows:
        log("Microsoft: no tiles cover this area")
        return []
    log(f"Microsoft: {len(rows)} tile file(s) cover the selection")

    buildings = []
    for i, row in enumerate(rows):
        if is_cancelled():
            raise Cancelled()
        safe_loc = re.sub(r"[^A-Za-z0-9_-]", "_", row["Location"])
        dest = cache_dir / f"{row['QuadKey']}_{safe_loc}.csv.gz"
        if not dest.exists():
            log(f"Microsoft: downloading tile {row['QuadKey']} ({row['Location']}, {row['Size']})")
            _download_tile(row["Url"], dest, log, progress, is_cancelled)

        progress(None, f"Microsoft: scanning tile {i + 1}/{len(rows)}")
        count = 0
        with gzip.open(dest, "rb") as f:
            for n, line in enumerate(f):
                if n % 20000 == 0 and is_cancelled():
                    raise Cancelled()
                m = _FIRST_COORD.search(line)
                if not m:
                    continue
                lon, lat = float(m.group(1)), float(m.group(2))
                if not (south - pad <= lat <= north + pad and west - pad <= lon <= east + pad):
                    continue
                feat = json.loads(line)
                g = feat.get("geometry") or {}
                if g.get("type") == "Polygon":
                    polys = [g["coordinates"]]
                elif g.get("type") == "MultiPolygon":
                    polys = g["coordinates"]
                else:
                    continue
                height = (feat.get("properties") or {}).get("height", -1)
                for poly in polys:
                    outer = poly[0]
                    if len(outer) < 4:
                        continue
                    pts = outer[:-1] if outer[0] == outer[-1] else outer
                    cx = sum(p[0] for p in pts) / len(pts)
                    cy = sum(p[1] for p in pts) / len(pts)
                    if not geo.point_in_ring(cx, cy, sel):
                        continue
                    buildings.append({
                        "id": f"ms/{cy:.6f},{cx:.6f}",
                        "source": "microsoft",
                        "tags": {"building": "yes"},
                        "ms_height": height if height and height > 0 else None,
                        "ring": [(p[1], p[0]) for p in pts],
                    })
                    count += 1
        log(f"Microsoft: tile {row['QuadKey']} -> {count} buildings in selection")
    return buildings
