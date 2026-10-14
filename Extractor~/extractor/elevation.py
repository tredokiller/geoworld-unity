"""Ground elevation (metres above sea level) from public digital elevation models.

Both services are free and need no key. They are tried in order; one that fails
is skipped for the rest of the run. Answers are cached on disk, so re-extracting
an area costs no requests.
"""
import json
import time
import urllib.error
import urllib.request

from .common import Cancelled, USER_AGENT

BATCH = 100        # locations per request, the limit of both services
KEY_DIGITS = 5     # ~1 m; the models are 30 to 90 m resolution, so this only merges duplicates
TIMEOUT = 40
SAVE_EVERY = 20    # batches between cache saves


class _OpenTopoData:
    name = "srtm30m"
    label = "SRTM 30 m (OpenTopoData)"
    attribution = "SRTM 30 m via OpenTopoData (api.opentopodata.org)"
    min_interval = 1.1  # public instance allows 1 request per second

    def url(self, pts):
        locs = "|".join(f"{lat:.{KEY_DIGITS}f},{lon:.{KEY_DIGITS}f}" for lat, lon in pts)
        return f"https://api.opentopodata.org/v1/srtm30m?locations={locs}"

    def parse(self, data, n):
        if data.get("status") != "OK":
            raise ValueError(data.get("error") or "unexpected response")
        values = [r.get("elevation") for r in data["results"]]
        if len(values) != n:
            raise ValueError("wrong number of results")
        return values


class _OpenMeteo:
    name = "copernicus90m"
    label = "Copernicus GLO-90 (Open-Meteo)"
    attribution = "Copernicus DEM GLO-90 via Open-Meteo (open-meteo.com)"
    min_interval = 0.2

    def url(self, pts):
        lats = ",".join(f"{lat:.{KEY_DIGITS}f}" for lat, _ in pts)
        lons = ",".join(f"{lon:.{KEY_DIGITS}f}" for _, lon in pts)
        return f"https://api.open-meteo.com/v1/elevation?latitude={lats}&longitude={lons}"

    def parse(self, data, n):
        values = data["elevation"]
        if len(values) != n:
            raise ValueError("wrong number of results")
        return values


PROVIDERS = [_OpenTopoData(), _OpenMeteo()]


def _key(lat, lon):
    return f"{lat:.{KEY_DIGITS}f},{lon:.{KEY_DIGITS}f}"


def _load_cache(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def _save_cache(path, cache):
    try:
        path.parent.mkdir(exist_ok=True)
        path.write_text(json.dumps(cache, separators=(",", ":")), encoding="utf-8")
    except OSError:
        pass  # a missing cache only costs speed


def _sleep(seconds, is_cancelled):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if is_cancelled():
            raise Cancelled()
        time.sleep(min(0.2, max(0.0, end - time.monotonic())))


def _request(provider, pts, is_cancelled):
    req = urllib.request.Request(provider.url(pts), headers={"User-Agent": USER_AGENT})
    for attempt in range(2):
        try:
            with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
                return provider.parse(json.loads(resp.read()), len(pts))
        except urllib.error.HTTPError as e:
            if e.code == 429 and attempt == 0:  # rate limited: wait once and retry
                _sleep(5, is_cancelled)
                continue
            raise


def fetch(points, cache_dir, log, progress, is_cancelled):
    """Looks up elevations for [(lat, lon), ...].

    Returns (values, sources): values is aligned with `points` (None where no
    data could be had); sources lists the labels of the models that answered.
    """
    cache_path = cache_dir / "elevation.json"
    cache = _load_cache(cache_path)
    keys = [_key(lat, lon) for lat, lon in points]

    todo = {}
    for k, (lat, lon) in zip(keys, points):
        if k not in cache and k not in todo:
            todo[k] = (round(lat, KEY_DIGITS), round(lon, KEY_DIGITS))
    todo_keys = list(todo)
    log(f"Elevation: {len(set(keys))} distinct points, {len(todo_keys)} to look up")

    dead = set()
    last_call = {}
    batches = [todo_keys[i:i + BATCH] for i in range(0, len(todo_keys), BATCH)]
    try:
        for n, batch in enumerate(batches):
            if is_cancelled():
                raise Cancelled()
            progress(n / len(batches), f"Elevation: request {n + 1}/{len(batches)}")
            pts = [todo[k] for k in batch]
            for provider in PROVIDERS:
                if provider.name in dead:
                    continue
                wait = provider.min_interval - (time.monotonic() - last_call.get(provider.name, -1e9))
                if wait > 0:
                    _sleep(wait, is_cancelled)
                last_call[provider.name] = time.monotonic()
                try:
                    values = _request(provider, pts, is_cancelled)
                except Cancelled:
                    raise
                except (urllib.error.URLError, TimeoutError, OSError, ValueError, KeyError) as e:
                    dead.add(provider.name)
                    log(f"Elevation: {provider.label} failed ({e}), switching")
                    continue
                for k, v in zip(batch, values):
                    if isinstance(v, (int, float)):
                        cache[k] = [float(v), provider.name]
                break
            else:
                log("Elevation: every service failed")
                break
            if (n + 1) % SAVE_EVERY == 0:
                _save_cache(cache_path, cache)
    finally:
        _save_cache(cache_path, cache)

    by_name = {p.name: p.label for p in PROVIDERS}
    values, used = [], []
    for k in keys:
        hit = cache.get(k)
        values.append(hit[0] if hit else None)
        if hit and by_name.get(hit[1]) not in used:
            used.append(by_name.get(hit[1]))
    progress(1.0, "Elevation: done")
    return values, [u for u in used if u]
