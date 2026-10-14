"""Estimated war damage from Sentinel-1 radar (open data, nothing to register for).

Radar sees buildings as bright, structured echoes; a building that is hit stops echoing the same way.
Comparing how every 10 m pixel behaved during the year before the invasion with how it behaves in the
latest weeks therefore shows where something changed. This follows the pixel-wise t-test of
Ballinger et al. (2024, "Open Access Battle Damage Detection via Pixel-Wise T-Test on Sentinel-1 Imagery").

Data: Sentinel-1 radiometrically terrain corrected scenes, "Contains modified Copernicus Sentinel data",
processed and served by Microsoft Planetary Computer (CC BY 4.0). They are read through its public STAC
API with an anonymous, short-lived token, and only the part of each scene that covers the area is fetched.

What this is not: a count of damaged houses. A pixel is 10 m wide, a Ukrainian house is about one pixel,
and trees, building work and snow also change the echo. The result is an estimate with low confidence,
good for "this district is badly hit" and weak for a single house; condition.py never lets it outweigh
a reviewed observation or a report.
"""
import hashlib
import json
import math
import time
import urllib.error
import urllib.request
import zlib
from array import array
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

from . import cog, raster
from .common import USER_AGENT, Cancelled

STAC_URL = "https://planetarycomputer.microsoft.com/api/stac/v1/search"
TOKEN_URL = "https://planetarycomputer.microsoft.com/api/sas/v1/token/sentinel-1-rtc"
COLLECTION = "sentinel-1-rtc"
ATTRIBUTION = ("Contains modified Copernicus Sentinel data, processed by Microsoft Planetary Computer "
               "(Sentinel-1 RTC, CC BY 4.0)")
PIXEL = 10.0
METHOD = "pixel-wise t-test, VV and VH, Ballinger et al. 2024"
FULL_SCALE = 12.0  # a smoothed |t| of this size or more is stored as 255
# Defaults for turning a score into a state. Scores are |t| values: 3.3 is the p < 0.001 line of the test.
# "intact" is off (0): at 10 m a quiet echo is no evidence that a small house is whole. Thresholds can be
# calibrated against published statistics, per building category (tools/calibrate_condition.py).
THRESHOLDS = {"intact": 0.0, "damaged": 3.3, "heavily_damaged": 6.0, "destroyed": 9.0}
MIN_BASELINE, MIN_RECENT = 12, 3  # scenes of one orbit needed before the invasion and in the monitoring window


# ------------------------------------------------------------------ projection

def utm_zone(lon):
    return int((lon + 180.0) // 6.0) + 1


_A, _F, _K0 = 6378137.0, 1 / 298.257223563, 0.9996
_N = _F / (2 - _F)
_BIG_A = _A / (1 + _N) * (1 + _N ** 2 / 4 + _N ** 4 / 64)
_ALPHA = (_N / 2 - 2 * _N ** 2 / 3 + 5 * _N ** 3 / 16, 13 * _N ** 2 / 48 - 3 * _N ** 3 / 5, 61 * _N ** 3 / 240)
_BETA = (_N / 2 - 2 * _N ** 2 / 3 + 37 * _N ** 3 / 96, _N ** 2 / 48 + _N ** 3 / 15, 17 * _N ** 3 / 480)
_DELTA = (2 * _N - 2 * _N ** 2 / 3 - 2 * _N ** 3, 7 * _N ** 2 / 3 - 8 * _N ** 3 / 5, 56 * _N ** 3 / 15)


def utm_forward(lat, lon, zone):
    """Easting and northing (metres, northern hemisphere) of a WGS84 position in a UTM zone (Kruger series)."""
    lon0 = math.radians((zone - 1) * 6 - 180 + 3)
    phi, lam = math.radians(lat), math.radians(lon) - lon0
    q = 2 * math.sqrt(_N) / (1 + _N)
    t = math.sinh(math.atanh(math.sin(phi)) - q * math.atanh(q * math.sin(phi)))
    xi, eta = math.atan2(t, math.cos(lam)), math.atanh(math.sin(lam) / math.sqrt(1 + t * t))
    x = eta + sum(_ALPHA[j] * math.cos(2 * (j + 1) * xi) * math.sinh(2 * (j + 1) * eta) for j in range(3))
    y = xi + sum(_ALPHA[j] * math.sin(2 * (j + 1) * xi) * math.cosh(2 * (j + 1) * eta) for j in range(3))
    return 500000.0 + _K0 * _BIG_A * x, _K0 * _BIG_A * y


def utm_inverse(easting, northing, zone):
    lon0 = math.radians((zone - 1) * 6 - 180 + 3)
    xi, eta = northing / (_K0 * _BIG_A), (easting - 500000.0) / (_K0 * _BIG_A)
    xi_p = xi - sum(_BETA[j] * math.sin(2 * (j + 1) * xi) * math.cosh(2 * (j + 1) * eta) for j in range(3))
    eta_p = eta - sum(_BETA[j] * math.cos(2 * (j + 1) * xi) * math.sinh(2 * (j + 1) * eta) for j in range(3))
    chi = math.asin(math.sin(xi_p) / math.cosh(eta_p))
    phi = chi + sum(_DELTA[j] * math.sin(2 * (j + 1) * chi) for j in range(3))
    lam = lon0 + math.atan2(math.sinh(eta_p), math.cos(xi_p))
    return math.degrees(phi), math.degrees(lam)


# ------------------------------------------------------------------ the archive

def _json(url, body=None, retries=3):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"User-Agent": USER_AGENT, **({"Content-Type": "application/json"} if body is not None else {})}
    for attempt in range(retries):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers), timeout=90) as resp:
                return json.loads(resp.read())
        except (urllib.error.URLError, TimeoutError, OSError):
            if attempt + 1 == retries:
                raise
            time.sleep(2 * (attempt + 1))


class Signer:
    """Anonymous read tokens of the Planetary Computer; they last about an hour and are renewed early."""

    def __init__(self):
        self._token, self._expires = None, 0.0

    def __call__(self, href):
        if self._token is None or time.time() > self._expires - 600:
            reply = _json(TOKEN_URL)
            self._token = reply["token"]
            expiry = datetime.strptime(reply["msft:expiry"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
            self._expires = expiry.timestamp()
        return href + "?" + self._token


class SignedCog(cog.Cog):
    """A Cog whose requests carry a fresh token while its cache keys use the plain address."""

    def __init__(self, url, cache_dir, signer):
        self._signer = signer
        super().__init__(url, cache_dir)

    @property
    def url(self):
        return self._signer(self._plain)

    @url.setter
    def url(self, value):
        self._plain = value


def search(bbox, start, end, relative_orbit=None, log=print):
    """Scenes that reach into the area between two dates (YYYY-MM-DD), newest first.

    bbox is (west, south, east, north) in degrees. A scene may cover only part of the area (an area on the edge
    of a frame is seen by two): every pixel is compared over the scenes that cover it.
    """
    body = {"collections": [COLLECTION], "bbox": list(bbox), "datetime": f"{start}T00:00:00Z/{end}T23:59:59Z",
            "limit": 200, "sortby": [{"field": "datetime", "direction": "desc"}]}
    if relative_orbit is not None:
        body["query"] = {"sat:relative_orbit": {"eq": int(relative_orbit)}}
    found, page = [], _json(STAC_URL, body)
    while True:
        found.extend(page.get("features", []))
        nxt = next((link for link in page.get("links", []) if link.get("rel") == "next"), None)
        if not nxt or not page.get("features"):
            break
        page = _json(nxt["href"], nxt.get("body")) if nxt.get("method") == "POST" else _json(nxt["href"])
    return found


def scene_zone(item):
    """The UTM zone of a scene's grid (EPSG 326xx), or None when the item does not say.

    A scene is stored in the zone of its own frame, which near 24, 30 and 36 degrees east is not always the
    zone of the area; its pixels can only be compared on a grid of the same zone.
    """
    for where in (item.get("properties") or {}, *(item.get("assets") or {}).values()):
        if not isinstance(where, dict):
            continue
        code = where.get("proj:epsg") or where.get("proj:code")
        if isinstance(code, str) and code.upper().startswith("EPSG:"):
            code = code[5:]
        try:
            code = int(code)
        except (TypeError, ValueError):
            continue
        if 32601 <= code <= 32660:
            return code - 32600
    return None


def _orbit_counts(items):
    counts = {}
    for item in items:
        orbit = item["properties"]["sat:relative_orbit"]
        counts[orbit] = counts.get(orbit, 0) + 1
    return counts


def pick_orbit(baseline_counts, recent_counts):
    """The orbit with the most baseline scenes among those that also have enough recent ones."""
    usable = [o for o in baseline_counts if baseline_counts[o] >= MIN_BASELINE and recent_counts.get(o, 0) >= MIN_RECENT]
    if not usable:
        seen = ", ".join(f"orbit {o}: {baseline_counts[o]} before, {recent_counts.get(o, 0)} recent"
                         for o in sorted(baseline_counts)) or "none"
        raise RuntimeError(f"No Sentinel-1 orbit has {MIN_BASELINE} scenes before the invasion and {MIN_RECENT} recent ones "
                           f"over this area ({seen})")
    return max(usable, key=lambda o: (baseline_counts[o], recent_counts.get(o, 0)))


def best_orbit(bbox, baseline, recent, log=print):
    """The relative orbit to compare: enough scenes in both windows, and the most before the invasion."""
    return pick_orbit(_orbit_counts(search(bbox, *baseline)), _orbit_counts(search(bbox, *recent)))


# ------------------------------------------------------------------ the grid and the analysis

class Grid:
    """A window of the UTM 10 m grid: columns grow east from `e0`, rows grow south from `n1`."""

    def __init__(self, zone, e0, n1, width, height):
        self.zone, self.e0, self.n1, self.width, self.height = zone, e0, n1, width, height

    @classmethod
    def around(cls, bbox, margin=0.0, zone=None):
        west, south, east, north = bbox
        zone = zone or utm_zone((west + east) / 2)
        corners = [utm_forward(la, lo, zone) for la in (south, north) for lo in (west, east)]
        e0 = math.floor((min(c[0] for c in corners) - margin) / PIXEL) * PIXEL
        e1 = math.ceil((max(c[0] for c in corners) + margin) / PIXEL) * PIXEL
        n0 = math.floor((min(c[1] for c in corners) - margin) / PIXEL) * PIXEL
        n1 = math.ceil((max(c[1] for c in corners) + margin) / PIXEL) * PIXEL
        return cls(zone, e0, n1, int((e1 - e0) / PIXEL), int((n1 - n0) / PIXEL))

    def to_json(self):
        return {"zone": self.zone, "e0": self.e0, "n1": self.n1, "width": self.width, "height": self.height, "pixel": PIXEL}

    @classmethod
    def from_json(cls, d):
        return cls(d["zone"], d["e0"], d["n1"], d["width"], d["height"])

    def cell(self, lat, lon):
        """Fractional column and row of a position, where integer + 0.5 is a pixel centre."""
        e, n = utm_forward(lat, lon, self.zone)
        return (e - self.e0) / PIXEL, (self.n1 - n) / PIXEL


class _Stats:
    def __init__(self, size):
        self.n = array("i", bytes(4 * size))
        self.s1 = array("d", bytes(8 * size))
        self.s2 = array("d", bytes(8 * size))

    def add(self, window):
        n, s1, s2, log10 = self.n, self.s1, self.s2, math.log10
        for i, v in enumerate(window):
            if v > 1e-5:
                d = 10.0 * log10(v)
                n[i] += 1
                s1[i] += d
                s2[i] += d * d


def _window(item, pol, grid, cache_dir, signer, is_cancelled, report):
    """Linear backscatter of one scene and polarisation over the grid (0, not counted, where the scene does not
    reach), or None when it misses the grid altogether."""
    href = item["assets"][pol]["href"]
    c = SignedCog(href, cache_dir, signer)
    if c.sx != PIXEL or abs(c.x0 % PIXEL) > 1e-6 or abs(c.y0 % PIXEL) > 1e-6:
        return None  # not on the common grid; the scenes of this archive all are
    col0, row0 = int(round((grid.e0 - c.x0) / PIXEL)), int(round((c.y0 - grid.n1) / PIXEL))
    data, c0, r0, w, h = c.read_window(col0, row0, col0 + grid.width, row0 + grid.height, is_cancelled=is_cancelled, report=report)
    if not w or not h:
        return None
    if w == grid.width and h == grid.height:
        return data
    return paste(data, c0 - col0, r0 - row0, w, h, grid.width, grid.height)


def paste(data, x, y, w, h, width, height):
    """A width x height window of zeros with the w x h `data` placed at column x, row y."""
    out = array("f", bytes(4 * width * height))
    for r in range(h):
        out[(y + r) * width + x:(y + r) * width + x + w] = data[r * w:(r + 1) * w]
    return out


def _box3(values, width, height):
    """3 x 3 mean, edges use what is there; speckle is the main noise of radar."""
    horizontal = array("f", bytes(4 * len(values)))
    for r in range(height):
        base = r * width
        row = values[base:base + width]
        horizontal[base:base + width] = array("f", [(row[max(i - 1, 0)] + row[i] + row[min(i + 1, width - 1)]) / 3.0
                                                    for i in range(width)])
    out = array("f", bytes(4 * len(values)))
    for r in range(height):
        up, mid, down = (max(r - 1, 0)) * width, r * width, (min(r + 1, height - 1)) * width
        out[mid:mid + width] = array("f", [(a + b + c) / 3.0 for a, b, c in
                                           zip(horizontal[up:up + width], horizontal[mid:mid + width], horizontal[down:down + width])])
    return out


def analyse(bbox, cache_dir, relative_orbit=None, baseline=("2021-02-24", "2022-02-23"), monitor_days=45,
            today=None, pols=("vv", "vh"), log=print, progress=lambda f, m=None: None, is_cancelled=lambda: False):
    """Change score map of the area: the |t| of the latest weeks against the year before the invasion.

    Returns a SarRaster. `bbox` is (west, south, east, north). The first call downloads a few MB per scene
    and polarisation (60 or so scenes of one orbit); everything is cached afterwards.
    """
    cache_dir = Path(cache_dir)
    today = today or date.today().isoformat()
    monitor_start = (date.fromisoformat(today) - timedelta(days=monitor_days)).isoformat()
    if relative_orbit is None:
        relative_orbit = best_orbit(bbox, baseline, (monitor_start, today), log)
    base_items = search(bbox, baseline[0], baseline[1], relative_orbit, log)
    mon_items = search(bbox, monitor_start, today, relative_orbit, log)
    # the grid takes the zone most scenes are stored in; a scene stored in another zone cannot be compared on it
    zones = [z for z in map(scene_zone, base_items + mon_items) if z]
    zone = max(set(zones), key=zones.count) if zones else None
    if zone:
        other = {i["id"] for i in base_items + mon_items if scene_zone(i) not in (None, zone)}
        if other:
            log(f"Radar: {len(other)} scenes are stored in another UTM zone than {zone} and are left out")
            base_items = [i for i in base_items if i["id"] not in other]
            mon_items = [i for i in mon_items if i["id"] not in other]
    log(f"Radar: orbit {relative_orbit}, {len(base_items)} baseline scenes, {len(mon_items)} from the last {monitor_days} days")
    if len(base_items) < MIN_BASELINE or len(mon_items) < MIN_RECENT:
        raise RuntimeError(f"Too few scenes (baseline {len(base_items)}, recent {len(mon_items)}) for orbit {relative_orbit}")
    grid = Grid.around(bbox, margin=60.0, zone=zone)
    size = grid.width * grid.height
    signer = Signer()
    jobs = [(item, pol, "base") for item in base_items for pol in pols] + [(item, pol, "mon") for item in mon_items for pol in pols]
    log(f"Radar: {grid.width} x {grid.height} pixels of 10 m, {len(jobs)} scene images to read")
    stats = {(pol, kind): _Stats(size) for pol in pols for kind in ("base", "mon")}
    got = [0]
    t0 = time.time()

    def report(nbytes):
        got[0] += nbytes
    for number, (item, pol, kind) in enumerate(jobs):
        if is_cancelled():
            raise Cancelled()
        window = _window(item, pol, grid, cache_dir, signer, is_cancelled, report)
        if window is None:
            log(f"Radar: skipped {item['id']} ({pol}): it does not cover the area on the common grid")
            continue
        stats[(pol, kind)].add(window)
        progress(0.9 * (number + 1) / len(jobs), f"Radar: scene {number // len(pols) + 1} of {len(jobs) // len(pols)} ({got[0] / 1e6:.0f} MB read)")
    log(f"Radar: read {got[0] / 1e6:.0f} MB in {time.time() - t0:.0f} s")

    score = array("f", bytes(4 * size))
    signed = array("f", bytes(4 * size))
    counted = 0
    for pol in pols:
        b, m = stats[(pol, "base")], stats[(pol, "mon")]
        for i in range(size):
            nb, nm = b.n[i], m.n[i]
            if nb < MIN_BASELINE or nm < 2:
                continue
            mean_b, mean_m = b.s1[i] / nb, m.s1[i] / nm
            var = max(b.s2[i] / nb - mean_b * mean_b, 0.0) * nb / (nb - 1)
            sd = max(math.sqrt(var), 0.7)  # a floor: a pixel that never varied would otherwise explode
            t = (mean_m - mean_b) / (sd * math.sqrt(1.0 / nm + 1.0 / nb))
            score[i] += abs(t) / len(pols)
            signed[i] += t / len(pols)
            counted += 1
    if not counted:
        raise RuntimeError("No pixel had enough scenes")
    smooth = _box3(score, grid.width, grid.height)
    progress(1.0, "Radar: done")
    meta = {"id": f"sar-{relative_orbit}", "kind": "sar_change", "method": METHOD, "relativeOrbit": relative_orbit,
            "orbitState": base_items[0]["properties"].get("sat:orbit_state"),
            "baseline": {"start": baseline[0], "end": baseline[1], "scenes": len(base_items)},
            "monitoring": {"start": monitor_start, "end": today, "scenes": len(mon_items),
                           "latest": max(i["properties"]["datetime"] for i in mon_items)[:10]},
            "polarisations": [p.upper() for p in pols], "grid": grid.to_json(), "scale": FULL_SCALE,
            "attribution": ATTRIBUTION, "createdAt": date.today().isoformat(),
            "license": {"name": "CC BY 4.0", "url": "https://creativecommons.org/licenses/by/4.0/"}}
    return SarRaster(meta, bytes(max(0, min(255, int(round(v / FULL_SCALE * 255)))) for v in smooth))


# ------------------------------------------------------------------ the product

class SarRaster:
    """A stored score map (8 bit, |t| / 12 * 255) with its meta data; the evidence condition.py reads."""

    def __init__(self, meta, data):
        self.meta = meta
        self.data = data
        self.grid = Grid.from_json(meta["grid"])
        self.thresholds = {**THRESHOLDS, **(meta.get("thresholds") or {})}
        self.category_thresholds = dict(meta.get("categoryThresholds") or {})

    @property
    def id(self):
        return self.meta["id"]

    @property
    def calibrated(self):
        """True when published figures for the area set the score lines (tools/calibrate_condition.py)."""
        return bool(self.meta.get("calibration"))

    # -- storing

    def save(self, folder):
        folder = Path(folder)
        folder.mkdir(parents=True, exist_ok=True)
        png, bounds = self.preview_png()
        self.meta["previewBounds"] = bounds
        (folder / f"{self.id}.bin.z").write_bytes(zlib.compress(self.data, 9))
        (folder / f"{self.id}.json").write_text(json.dumps(self.meta, indent=1), encoding="utf-8")
        (folder / f"{self.id}-preview.png").write_bytes(png)

    @classmethod
    def load(cls, folder, raster_id):
        folder = Path(folder)
        meta = json.loads((folder / f"{raster_id}.json").read_text(encoding="utf-8"))
        data = zlib.decompress((folder / f"{raster_id}.bin.z").read_bytes())
        if len(data) != meta["grid"]["width"] * meta["grid"]["height"]:
            raise ValueError("The radar raster does not match its description")
        return cls(meta, data)

    # -- what condition.py calls

    def describe(self):
        return {k: self.meta[k] for k in ("id", "kind", "method", "relativeOrbit", "baseline", "monitoring", "polarisations",
                                          "attribution", "license") if k in self.meta} | {
            "thresholds": self.thresholds, "categoryThresholds": self.category_thresholds,
            "calibration": self.meta.get("calibration", []), "calibrated": self.calibrated}

    def files(self):
        return {f"{self.id}-preview.png": self.preview_png()[0]}

    def score_at(self, lat, lon, radius_m=0.0):
        """Score at a position: the larger of the interpolated value and the mean within `radius_m`."""
        g = self.grid
        col, row = g.cell(lat, lon)
        fx, fy = col - 0.5, row - 0.5
        ix, iy = int(math.floor(fx)), int(math.floor(fy))
        if ix < 0 or iy < 0 or ix + 1 >= g.width or iy + 1 >= g.height:
            return None
        tx, ty = fx - ix, fy - iy
        d, w = self.data, g.width
        i = iy * w + ix
        centre = ((d[i] * (1 - tx) + d[i + 1] * tx) * (1 - ty) + (d[i + w] * (1 - tx) + d[i + w + 1] * tx) * ty) / 255.0 * FULL_SCALE
        if radius_m <= PIXEL:
            return centre
        r = radius_m / PIXEL
        total, count = 0, 0
        for yy in range(max(0, int(row - r)), min(g.height, int(row + r) + 1)):
            for xx in range(max(0, int(col - r)), min(g.width, int(col + r) + 1)):
                if (xx + 0.5 - col) ** 2 + (yy + 0.5 - row) ** 2 <= r * r:
                    total += d[yy * w + xx]
                    count += 1
        return max(centre, total / count / 255.0 * FULL_SCALE) if count else centre

    def state_for(self, score, category=None):
        t = {**self.thresholds, **self.category_thresholds.get(category, {})}
        if score >= t["destroyed"]:
            return "destroyed"
        if score >= t["heavily_damaged"]:
            return "heavily_damaged"
        if score >= t["damaged"]:
            return "damaged"
        if t["intact"] and score < t["intact"]:
            return "intact"
        return None  # between the lines: the echo changed a little, which says nothing

    def estimates(self, export):
        """{building id: evidence} for the buildings of an export that lie inside this raster."""
        result = {}
        monitoring = self.meta["monitoring"]
        note = "" if self.calibrated else " The score lines are the general ones, not calibrated for this area."
        for b in export.get("buildings", []):
            radius = max(b.get("width", 0), b.get("length", 0)) / 2.0 + 5.0
            score = self.score_at(b["lat"], b["lon"], radius)
            if score is None:
                continue
            state = self.state_for(score, b.get("category"))
            if state is None:
                continue
            result[b["id"]] = {"state": state, "basis": "estimated", "confidence": "low", "score": round(score, 2),
                               "asOf": monitoring.get("latest") or monitoring["end"], "sourceId": self.id, "assessmentId": None,
                               "note": f"Radar change score {score:.1f} (Sentinel-1 orbit {self.meta['relativeOrbit']})." + note}
        return result

    def source(self):
        """The condition catalog source entry that stands for this raster."""
        return {"id": self.id, "kind": "satellite", "title": f"Sentinel-1 radar change, orbit {self.meta['relativeOrbit']}",
                "publisher": "ESA Copernicus / Microsoft Planetary Computer",
                "url": "https://planetarycomputer.microsoft.com/dataset/sentinel-1-rtc",
                "publishedAt": self.meta["monitoring"].get("latest"), "license": self.meta["license"],
                "note": self.meta["attribution"] + ". " + self.meta["method"] + "."}

    # -- looking at it

    def preview_png(self, size=800):
        """A north-up transparent heat map and its [[south, west], [north, east]] bounds, for the map view."""
        g = self.grid
        corners = [utm_inverse(g.e0 + dx * g.width * PIXEL, g.n1 - dy * g.height * PIXEL, g.zone) for dx in (0, 1) for dy in (0, 1)]
        south, north = min(c[0] for c in corners), max(c[0] for c in corners)
        west, east = min(c[1] for c in corners), max(c[1] for c in corners)
        width = size
        height = max(1, int(round(size * (north - south) / ((east - west) * math.cos(math.radians((south + north) / 2))))))
        rgba = bytearray(width * height * 4)
        data, w = self.data, g.width
        for y in range(height):
            lat = north - (y + 0.5) / height * (north - south)
            for x in range(width):
                lon = west + (x + 0.5) / width * (east - west)
                e, n = utm_forward(lat, lon, g.zone)
                col, row = int((e - g.e0) / PIXEL), int((g.n1 - n) / PIXEL)
                if 0 <= col < w and 0 <= row < g.height:
                    v = data[row * w + col] / 255.0 * FULL_SCALE
                    if v >= self.thresholds["damaged"] * 0.6:
                        k = min(1.0, (v - self.thresholds["damaged"] * 0.6) / (self.thresholds["destroyed"] - self.thresholds["damaged"] * 0.6))
                        o = (y * width + x) * 4
                        rgba[o:o + 4] = bytes((255, int(230 - 200 * k), int(40 * (1 - k)), int(90 + 140 * k)))
        return raster.png_rgba(width, height, bytes(rgba)), [[south, west], [north, east]]
