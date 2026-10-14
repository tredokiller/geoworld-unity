"""Local web app: draw an area on a map of Ukraine, extract buildings, export for Unity.

Run:  py -3 server.py [--port 8765] [--open]
"""
import argparse
import json
import re
import shutil
import socket
import threading
import time
import traceback
import uuid
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlparse

from extractor import appearance, condition, environment, news, package, pipeline, roofs, sar, street_reference, world
from extractor.common import Cancelled

ROOT = Path(__file__).resolve().parent
WEB = ROOT / "web"
HOME = package.home(ROOT)  # cache and exports; the bundled data stays in ROOT/data
CACHE = HOME / "cache"
EXPORTS = HOME / "exports"
PREVIEWS = CACHE / "previews"
REFERENCES = ROOT / "data" / "street_reference"
REFERENCE_STATE = CACHE / "street_reference"
CONDITIONS = ROOT / "data" / "condition"
CONDITION_STATE = CACHE / "condition"
MAX_JOBS = 6

MIME = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".json": "application/json",
    ".svg": "image/svg+xml",
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".ico": "image/x-icon",
}


class Job:
    def __init__(self, params):
        self.id = uuid.uuid4().hex[:12]
        self.params = params
        self.status = "running"
        self.progress = 0.0
        self.stage = "Starting"
        self.log = []
        self.error = None
        self.export_path = None
        self.summary = None
        self.cancelled = False
        self.created = time.time()

    def to_status(self, since=0):
        return {
            "id": self.id,
            "status": self.status,
            "progress": round(self.progress, 4),
            "stage": self.stage,
            "log": self.log[since:],
            "logCount": len(self.log),
            "error": self.error,
            "summary": self.summary,
            "exportPath": str(self.export_path) if self.export_path else None,
            "polygon": self.params.get("polygon"),
        }


JOBS = {}
JOBS_LOCK = threading.Lock()


def _env_dir(path):
    return EXPORTS / f"{path.stem}_env"


def _env_summary(path):
    """What the page needs to show the terrain and ground package that goes with an export, or None."""
    folder = _env_dir(path)
    try:
        env = json.loads((folder / "environment.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    mats, terrain = env.get("materials") or {}, env.get("terrain") or {}
    base = f"/env/{folder.name}/"
    overlays = {}
    if mats.get("previewFile"):
        overlays["materials"] = {"url": base + mats["previewFile"], "bounds": mats.get("previewBounds")}
    if terrain.get("previewFile"):
        overlays["hillshade"] = {"url": base + terrain["previewFile"], "bounds": terrain.get("previewBounds")}
    return {
        "folder": folder.name,
        "zip": f"{path.stem}.zip",
        "zipSizeMB": round(sum(f.stat().st_size for f in folder.iterdir() if f.is_file()) / 1e6, 1),
        "summary": env.get("summary") or {},
        "legend": mats.get("classes") or [],
        "overlays": overlays,
        "warnings": env.get("warnings") or [],
    }


def _env_geojson(path):
    """Map preview of a package's roads, water, land areas and trees; cached next to the other previews."""
    src = _env_dir(path) / "environment.json"
    PREVIEWS.mkdir(parents=True, exist_ok=True)
    out = PREVIEWS / f"{path.stem}.env.geojson"
    if not out.exists() or out.stat().st_mtime < src.stat().st_mtime:
        geo = environment.preview_geojson(json.loads(src.read_text(encoding="utf-8")))
        out.write_text(json.dumps(geo, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    return out


def _summarize(export, path):
    cats = {}
    for b in export["buildings"]:
        cats[b["category"]] = cats.get(b["category"], 0) + 1
    return {
        "total": len(export["buildings"]),
        "sources": {s["name"]: s["count"] for s in export["sources"]},
        "categories": dict(sorted(cats.items(), key=lambda kv: -kv[1])),
        "origin": export["origin"],
        "areaKm2": export["area"]["areaKm2"],
        "fileName": path.name,
        "fileSizeMB": round(path.stat().st_size / 1e6, 2),
        "warnings": export.get("warnings", []),
        "environment": _env_summary(path),
        "referenceEnrichment": (export.get("streetReferences") or {}).get("summary"),
        "referencePackageUrl": f"/exports/{path.stem}.zip" if (_env_dir(path) / "street-references.json").exists() else None,
        "appearance": appearance.summary(export["buildings"]),
        "roofs": roofs.summary(export["buildings"]),
        "condition": condition.summary(export["buildings"]),
        "conditionLayer": (export.get("conditionLayer") or {}).get("summary"),
        "conditionArea": _condition_area(export),
        "conflicts": {k: v for k, v in (export.get("conflicts") or {}).items() if k != "dropped"} or None,
    }


def _condition_area(export):
    """The condition area that covers an export ({id, name, radar}), or None when there is none yet."""
    catalog = _optional(condition.find_catalog, CONDITIONS, CONDITION_STATE, export)
    if not catalog:
        return None
    return {"id": catalog["area"]["id"], "name": catalog["area"].get("name", catalog["area"]["id"]),
            "radar": bool(catalog.get("rasters"))}


def estimate_radar(export, name, log, progress, is_cancelled, orbit=None, reuse=True):
    """The radar estimate of the area of an export (condition.estimate_radar with the app's folders)."""
    return condition.estimate_radar(CONDITIONS, CONDITION_STATE, CACHE, export, name, log, progress, is_cancelled,
                                    orbit=orbit, reuse=reuse)


def _write_preview(export_path, features):
    PREVIEWS.mkdir(parents=True, exist_ok=True)
    (PREVIEWS / f"{export_path.stem}.geojson").write_text(
        json.dumps({"type": "FeatureCollection", "features": features}, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8")


def open_export(path):
    """Summary + selection polygon; refresh preview when data or schema changes."""
    export = json.loads(path.read_text(encoding="utf-8"))
    if export.get("format") != pipeline.FORMAT:
        raise ValueError(f"{path.name} is not a building export")
    preview_path = PREVIEWS / f"{path.stem}.geojson"
    # rebuilt when the export or how it is drawn changed (an extraction used to save it before the condition was added)
    newest = max(path.stat().st_mtime, Path(pipeline.__file__).stat().st_mtime, Path(condition.__file__).stat().st_mtime)
    if not preview_path.exists() or preview_path.stat().st_mtime < newest:
        _write_preview(path, pipeline.preview_from_export(export))
    flat = export["area"]["polygon"]
    return {
        "summary": _summarize(export, path),
        "exportPath": str(path),
        "polygon": [[flat[i], flat[i + 1]] for i in range(0, len(flat) - 1, 2)],
    }


def list_exports(limit=15):
    if not EXPORTS.exists():
        return []
    files = sorted(EXPORTS.glob("*.json"), key=lambda p: p.stat().st_mtime, reverse=True)[:limit]
    return [{
        "fileName": p.name,
        "sizeMB": round(p.stat().st_size / 1e6, 2),
        "modified": time.strftime("%Y-%m-%d %H:%M", time.localtime(p.stat().st_mtime)),
    } for p in files]


def _inside(folder, name):
    """Resolves name inside folder; None if it escapes the folder or doesn't exist."""
    target = (folder / name).resolve()
    if target.parent != folder.resolve() or not target.is_file():
        return None
    return target


def _optional(read, *args):
    try:
        return read(*args)
    except (OSError, ValueError, KeyError):
        return None


def build_copy(path, references, conditions, label):
    """A new export with the reviewed photo links and/or the building condition applied.

    The original export and its source evidence stay intact. Either catalog may be None.
    """
    export = json.loads(path.read_text(encoding="utf-8"))
    if export.get("format") != pipeline.FORMAT:
        raise ValueError("Choose a building export")
    use_references = references is not None and street_reference.overlaps(export, references)
    use_condition = conditions is not None and condition.overlaps(export, conditions)
    if not use_references and not use_condition:
        raise ValueError("There is no building condition for this area yet. Estimate it from radar first.")
    original_env = _env_dir(path)
    env_path = original_env / "environment.json"
    env = json.loads(env_path.read_text(encoding="utf-8")) if env_path.exists() else None
    stats, condition_stats, extra = None, None, {}
    if use_references:
        stats = street_reference.enrich(export, env, references)
        extra.update(street_reference.package_files(REFERENCES, references, REFERENCE_STATE))
    if use_condition:
        rasters = condition.load_rasters(conditions)
        condition_stats = condition.enrich(export, env, conditions, rasters)
        extra.update(condition.package_files(conditions, export, rasters))
    stamp = time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6]
    output = EXPORTS / f"{path.stem[:90]}_{label}_{stamp}.json"
    folder = _env_dir(output)
    try:
        folder.mkdir(parents=True)
        if env is not None:
            for source in original_env.iterdir():
                if source.is_file():
                    shutil.copyfile(source, folder / source.name)
        for name, data in extra.items():
            (folder / name).write_bytes(data)
        encoded = json.dumps(export, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        (folder / "buildings.json").write_bytes(encoded)
        if env is not None:
            (folder / "environment.json").write_text(json.dumps(env, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        (folder / "README.txt").write_text(package.README, encoding="utf-8")
        output.write_bytes(encoded)
        result = open_export(output)
        result["enrichment"] = stats
        result["conditionEnrichment"] = condition_stats
        result["referencePackageUrl"] = f"/exports/{output.stem}.zip"
        result["packageUrl"] = f"/exports/{output.stem}.zip"
        return result
    except Exception:
        # Do not leave a half-written result in the recent exports list.
        if output.exists():
            output.unlink()
        raise


def _read_export(path):
    return json.loads(path.read_text(encoding="utf-8"))


def enrich_copy(path, catalog):
    """The photo-reference button: reviewed links, plus the building condition where it is known."""
    conditions = _optional(condition.find_catalog, CONDITIONS, CONDITION_STATE, _read_export(path))
    return build_copy(path, catalog, conditions, "photo")


def condition_copy(path):
    """The condition button: the building condition of the area that covers the export, plus the reviewed photo
    links where they apply."""
    references = _optional(street_reference.read_catalog, REFERENCES, REFERENCE_STATE, "lyman", False)
    return build_copy(path, references, condition.find_catalog(CONDITIONS, CONDITION_STATE, _read_export(path)), "condition")


def run_job(job):
    p = job.params

    def log(msg):
        job.log.append(f"{time.strftime('%H:%M:%S')}  {msg}")

    def progress(frac, msg=None):
        if frac is not None:
            job.progress = max(job.progress, min(frac, 1.0))
        if msg:
            job.stage = msg

    radar = bool(p.get("radar"))
    # with a radar estimate the extraction is the first 60% of the bar
    extract_progress = (lambda frac, msg=None: progress(None if frac is None else frac * 0.6, msg)) if radar else progress

    try:
        origin = p.get("origin")
        env_options = p.get("environment") or None
        result = world.extract(
            polygon=[(pt[0], pt[1]) for pt in p["polygon"]],
            sources=p.get("sources") or ["osm", "microsoft"],
            cache_dir=CACHE,
            origin=(origin["lat"], origin["lon"]) if origin else None,
            min_area=float(p.get("minArea", 6.0)),
            with_elevation=bool(p.get("elevation", True)),
            estimate_roofs=bool(p.get("estimateRoofs", True)),
            fix_conflicts=bool(p.get("fixConflicts", True)),
            env_options=env_options,
            log=log,
            progress=extract_progress,
            is_cancelled=lambda: job.cancelled,
        )
        export, preview = result.export, result.preview
        catalog = street_reference.read_catalog(REFERENCES, REFERENCE_STATE, public=False)
        reference_files = {}
        if street_reference.overlaps(export, catalog):
            counts = street_reference.enrich(export, result.env.json if result.env else None, catalog)
            log("Photo references: " + counts["message"])
            reference_files = street_reference.package_files(REFERENCES, catalog, REFERENCE_STATE)
            if result.env:
                result.env.files.update(reference_files)
        condition_catalog = None
        if radar:
            try:
                condition_catalog = estimate_radar(export, p.get("name") or "", log,
                                                   lambda frac, msg=None: progress(None if frac is None else 0.6 + 0.4 * frac, msg),
                                                   lambda: job.cancelled)
            except Cancelled:
                raise
            except Exception as e:  # the buildings are still worth saving: say why they have no estimate
                log(f"Radar estimate failed: {e}")
                export.setdefault("warnings", []).append(f"No radar estimate of the damage: {e}")
        if condition_catalog is None:
            condition_catalog = _optional(condition.find_catalog, CONDITIONS, CONDITION_STATE, export)
        if condition_catalog is not None and condition.overlaps(export, condition_catalog):
            rasters = condition.load_rasters(condition_catalog)
            counts = condition.enrich(export, result.env.json if result.env else None, condition_catalog, rasters)
            log("Building condition: " + counts["message"])
            condition_files = condition.package_files(condition_catalog, export, rasters)
            if result.env:
                result.env.files.update(condition_files)
            else:
                reference_files.update(condition_files)
        if job.cancelled:
            raise Cancelled()
        EXPORTS.mkdir(exist_ok=True)
        path = EXPORTS / (package.export_stem(p.get("name") or "", export["origin"]) + ".json")
        data = json.dumps(export, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        path.write_bytes(data)
        # the photo links and the condition were added after the extraction drew its preview: draw it again with them
        _write_preview(path, pipeline.preview_from_export(export) if export.get("conditionLayer") or export.get("streetReferences") else preview)
        if result.env:
            package.write(_env_dir(path), export, result.env)
        elif reference_files:
            folder = _env_dir(path)
            folder.mkdir(parents=True, exist_ok=True)
            for name, content in reference_files.items():
                (folder / name).write_bytes(content)
            (folder / "buildings.json").write_bytes(data)
            (folder / "README.txt").write_text(package.README, encoding="utf-8")
        job.export_path = path
        job.summary = _summarize(export, path)
        log(f"Saved {path.name} ({len(data) / 1e6:.2f} MB)")
        job.status = "done"
    except Cancelled:
        job.status = "cancelled"
    except Exception as e:  # report anything to the UI rather than dying silently
        if job.cancelled:
            return
        traceback.print_exc()
        job.error = str(e)
        log(f"ERROR: {e}")
        job.status = "error"


def _condition_public(catalog):
    """What the page needs of a condition catalog: its evidence and the radar products with their map bounds."""
    rasters = []
    for entry in catalog.get("rasters", []):
        try:
            meta = json.loads((Path(catalog["folder"]) / entry.get("folder", "sar") / f"{entry['id']}.json").read_text(encoding="utf-8"))
        except (OSError, ValueError, KeyError):
            continue
        rasters.append({
            "id": entry["id"], "relativeOrbit": meta.get("relativeOrbit"), "orbitState": meta.get("orbitState"),
            "baseline": meta.get("baseline"), "monitoring": meta.get("monitoring"),
            "thresholds": {**sar.THRESHOLDS, **(meta.get("thresholds") or {}), **(entry.get("thresholds") or {})},
            "bounds": meta.get("previewBounds"), "attribution": meta.get("attribution"),
            "calibration": entry.get("calibration", []), "calibrated": bool(entry.get("calibration")),
            "previewUrl": f"/api/condition/{catalog['area']['id']}/preview/{entry['id']}.png"})
    return {"area": catalog["area"], "sources": catalog["sources"], "assessments": catalog["assessments"],
            "rasters": rasters, "states": list(condition.STATES), "notes": catalog.get("notes", [])}


def run_radar_job(job):
    """Fresh radar estimate: reads the newest Sentinel-1 scenes and stores the score map.

    For an area of the catalogs (params area), or for an export (params fileName): then for the area that covers
    it, made from its bounds when there is none, and the export gets a new package with the condition.
    """
    p = job.params

    def log(msg):
        job.log.append(f"{time.strftime('%H:%M:%S')}  {msg}")

    def progress(frac, msg=None):
        if frac is not None:
            job.progress = max(job.progress, min(frac, 1.0))
        if msg:
            job.stage = msg

    try:
        if p.get("fileName"):
            target = _inside(EXPORTS, p["fileName"])
            if not target:
                raise FileNotFoundError("Export not found")
            export = _read_export(target)
            name = re.sub(r"_\d+\.\d+N_\d+\.\d+E_.*$", "", target.stem)
            catalog = estimate_radar(export, name, log, lambda frac, msg=None: progress(None if frac is None else 0.9 * frac, msg),
                                     lambda: job.cancelled, orbit=p.get("orbit"), reuse=False)
            progress(0.95, "Creating the package with condition")
            copy = condition_copy(target)
            log("Building condition: " + (copy.get("conditionEnrichment") or {}).get("message", "applied"))
            job.summary = {"area": catalog["area"]["id"], "copy": copy}
        else:
            catalog = condition.read_catalog(CONDITIONS, CONDITION_STATE, p["area"])
            b = catalog["area"]["analysisBounds"]
            result = sar.analyse((b["west"], b["south"], b["east"], b["north"]), CACHE, relative_orbit=p.get("orbit"),
                                 log=log, progress=progress, is_cancelled=lambda: job.cancelled)
            condition.store_raster(CONDITIONS, CONDITION_STATE, p["area"], result)
            job.summary = {"id": result.id, "latest": result.meta["monitoring"].get("latest"), "orbit": result.meta["relativeOrbit"]}
            log(f"Saved radar estimate {result.id} (newest scene {job.summary['latest']})")
        job.status = "done"
    except Cancelled:
        job.status = "cancelled"
    except Exception as e:
        if job.cancelled:
            return
        traceback.print_exc()
        job.error = str(e)
        log(f"ERROR: {e}")
        job.status = "error"


class LocalServer(ThreadingHTTPServer):
    def server_bind(self):
        # Windows otherwise allows two development servers on the same port,
        # silently routing requests to the older version of the app.
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.allow_reuse_address = False
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


class Handler(BaseHTTPRequestHandler):
    server_version = "BuildingExtractor/1.0"

    def log_message(self, fmt, *args):
        if "/status" not in self.path:  # progress polling would flood the console
            super().log_message(fmt, *args)

    def _send(self, code, body, ctype="application/json", extra=None):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode("utf-8")
        elif isinstance(body, str):
            body = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def _job(self, job_id):
        with JOBS_LOCK:
            return JOBS.get(job_id)

    def do_GET(self):
        url = urlparse(self.path)
        parts = [p for p in url.path.split("/") if p]

        if parts[:2] == ["api", "references"] or parts[:1] == ["reference-media"]:
            try:
                if len(parts) == 3 and parts[0] == "reference-media":
                    target = street_reference.media_file(REFERENCES, unquote(parts[1]), unquote(parts[2]), REFERENCE_STATE)
                    return self._send(200, target.read_bytes(), MIME.get(target.suffix.lower(), "image/jpeg"))
                if len(parts) in (3, 4) and parts[:2] == ["api", "references"]:
                    catalog = street_reference.read_catalog(REFERENCES, REFERENCE_STATE, parts[2])
                    if len(parts) == 3:
                        return self._send(200, catalog)
                    if parts[3] == "download":
                        return self._send(200, street_reference.zip_bytes(REFERENCES, catalog, REFERENCE_STATE), "application/zip", {
                            "Content-Disposition": f'attachment; filename="{parts[2]}-photo-references.zip"'})
                return self._send(404, {"error": "Photo library not found"})
            except FileNotFoundError:
                return self._send(404, {"error": "Photo library or image not found"})
            except (ValueError, KeyError) as e:
                return self._send(400, {"error": str(e)})

        if parts[:2] == ["api", "condition"] and len(parts) in (3, 4, 5):
            try:
                catalog = condition.read_catalog(CONDITIONS, CONDITION_STATE, unquote(parts[2]))
                if len(parts) == 3:
                    return self._send(200, _condition_public(catalog))
                if len(parts) == 4 and parts[3] == "news":
                    return self._send(200, news.cached(CONDITION_STATE / catalog["area"]["id"] / "news.json"))
                if parts[3] == "preview" and parts[4].endswith(".png") and re.fullmatch(r"[A-Za-z0-9_.-]+", parts[4]):
                    target = _inside(Path(catalog["folder"]) / "sar", f"{parts[4][:-4]}-preview.png")
                    if target:
                        return self._send(200, target.read_bytes(), "image/png")
                return self._send(404, {"error": "Not found"})
            except FileNotFoundError:
                return self._send(404, {"error": "Condition catalog not found"})
            except (ValueError, KeyError) as e:
                return self._send(400, {"error": str(e)})

        if parts[:2] == ["api", "jobs"] and len(parts) >= 3:
            job = self._job(parts[2])
            if not job:
                return self._send(404, {"error": "unknown job"})
            action = parts[3] if len(parts) > 3 else "status"
            if action == "status":
                since = 0
                if url.query.startswith("since="):
                    since = int(url.query[6:] or 0)
                return self._send(200, job.to_status(since))
            return self._send(404, {"error": "not available"})

        # Exports and their map previews live on disk, so they survive a server restart.
        if len(parts) == 2 and parts[0] == "exports" and parts[1].endswith(".zip"):
            folder = EXPORTS / f"{Path(unquote(parts[1])).stem}_env"
            if folder.resolve().parent != EXPORTS.resolve() or not folder.is_dir():
                return self._send(404, {"error": "not found"})
            return self._send(200, package.zip_bytes(folder), "application/zip", {
                "Content-Disposition": f'attachment; filename="{Path(unquote(parts[1])).stem}_terrain.zip"'})
        if len(parts) == 3 and parts[0] == "env":  # pictures of a package, shown as map overlays
            folder = EXPORTS / unquote(parts[1])
            if folder.resolve().parent != EXPORTS.resolve() or not folder.is_dir():
                return self._send(404, {"error": "not found"})
            target = _inside(folder, unquote(parts[2]))
            if not target or target.suffix.lower() != ".png":
                return self._send(404, {"error": "not found"})
            return self._send(200, target.read_bytes(), "image/png")
        if len(parts) == 4 and parts[:2] == ["api", "env"] and parts[3] == "preview":
            source = _inside(EXPORTS, Path(unquote(parts[2])).stem + ".json")
            if not source or not (_env_dir(source) / "environment.json").exists():
                return self._send(404, {"error": "not found"})
            return self._send(200, _env_geojson(source).read_bytes(), "application/geo+json")
        if len(parts) == 2 and parts[0] == "exports":
            target = _inside(EXPORTS, unquote(parts[1]))
            if not target:
                return self._send(404, {"error": "not found"})
            return self._send(200, target.read_bytes(), "application/json", {
                "Content-Disposition": f'attachment; filename="{target.name}"'})
        if parts == ["api", "exports"]:
            return self._send(200, list_exports())
        if len(parts) == 3 and parts[:2] == ["api", "open"]:
            target = _inside(EXPORTS, unquote(parts[2]))
            if not target:
                return self._send(404, {"error": "not found"})
            try:
                return self._send(200, open_export(target))
            except (ValueError, KeyError) as e:
                return self._send(400, {"error": str(e)})
        if len(parts) == 3 and parts[:2] == ["api", "previews"]:
            target = _inside(PREVIEWS, Path(unquote(parts[2])).stem + ".geojson")
            if not target:
                return self._send(404, {"error": "not found"})
            return self._send(200, target.read_bytes(), "application/geo+json")

        # Static files from web/
        rel = url.path.lstrip("/") or "index.html"
        target = (WEB / rel).resolve()
        if WEB.resolve() not in target.parents and target != WEB.resolve():
            return self._send(403, {"error": "forbidden"})
        if not target.is_file():
            return self._send(404, {"error": "not found"})
        self._send(200, target.read_bytes(), MIME.get(target.suffix.lower(), "application/octet-stream"))

    def do_HEAD(self):
        self.send_response(200 if urlparse(self.path).path in ("/", "/index.html") else 404)
        self.end_headers()

    def do_POST(self):
        url = urlparse(self.path)
        parts = [p for p in url.path.split("/") if p]
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            return self._send(400, {"error": "Invalid request size"})
        max_length = 18 * 1024 * 1024 if len(parts) == 4 and parts[:2] == ["api", "references"] and parts[3] == "photos" else 1024 * 1024
        if length < 0 or length > max_length:
            return self._send(413, {"error": "Request is too large"})
        raw = self.rfile.read(length) if length else b"{}"

        if parts[:2] == ["api", "references"]:
            origin = self.headers.get("Origin")
            if origin and origin not in {f"http://localhost:{self.server.server_port}", f"http://127.0.0.1:{self.server.server_port}"}:
                return self._send(403, {"error": "Open the local app to save photo observations"})
            if self.headers.get_content_type() != "application/json":
                return self._send(415, {"error": "Use a JSON request"})
            try:
                if len(parts) != 4:
                    return self._send(404, {"error": "Not found"})
                params = json.loads(raw)
                if parts[3] == "photos":
                    photo = street_reference.save_photo(REFERENCES, REFERENCE_STATE, parts[2], params)
                    return self._send(200, {"photo": photo})
                if parts[3] == "observations":
                    observation = street_reference.save_observation(REFERENCES, REFERENCE_STATE, parts[2], params)
                    return self._send(200, {"observation": observation})
                if parts[3] == "enrich":
                    if not isinstance(params, dict) or not isinstance(params.get("fileName"), str):
                        raise ValueError("Choose an export first")
                    target = _inside(EXPORTS, params["fileName"])
                    if not target:
                        raise FileNotFoundError("Export not found")
                    catalog = street_reference.read_catalog(REFERENCES, REFERENCE_STATE, parts[2], public=False)
                    return self._send(200, enrich_copy(target, catalog))
                return self._send(404, {"error": "Not found"})
            except FileNotFoundError:
                return self._send(404, {"error": "Photo library or export not found"})
            except (ValueError, TypeError, KeyError) as e:
                return self._send(400, {"error": str(e)})

        if parts[:2] == ["api", "condition"]:
            origin = self.headers.get("Origin")
            if origin and origin not in {f"http://localhost:{self.server.server_port}", f"http://127.0.0.1:{self.server.server_port}"}:
                return self._send(403, {"error": "Open the local app to save condition evidence"})
            if self.headers.get_content_type() != "application/json":
                return self._send(415, {"error": "Use a JSON request"})
            try:
                if parts == ["api", "condition", "radar"]:
                    params = json.loads(raw)
                    if not isinstance(params, dict) or not isinstance(params.get("fileName"), str) or not _inside(EXPORTS, params["fileName"]):
                        raise FileNotFoundError("Export not found")
                    # an area too large for an estimate is refused now, not after the job started
                    condition.radar_area(CONDITIONS, CONDITION_STATE, _read_export(_inside(EXPORTS, params["fileName"])), create=False)
                    orbit = params.get("orbit")
                    job = Job({"task": "radar", "fileName": params["fileName"], "orbit": int(orbit) if orbit else None})
                    with JOBS_LOCK:
                        JOBS[job.id] = job
                    threading.Thread(target=run_radar_job, args=(job,), daemon=True).start()
                    return self._send(200, {"id": job.id})
                if len(parts) != 4:
                    return self._send(404, {"error": "Not found"})
                area, params = unquote(parts[2]), json.loads(raw)
                if parts[3] == "assessments":
                    return self._send(200, {"assessment": condition.save_assessment(CONDITIONS, CONDITION_STATE, area, params)})
                if parts[3] == "sources":
                    return self._send(200, {"source": condition.save_source(CONDITIONS, CONDITION_STATE, area, params)})
                if parts[3] == "apply":
                    if not isinstance(params, dict) or not isinstance(params.get("fileName"), str):
                        raise ValueError("Choose an export first")
                    target = _inside(EXPORTS, params["fileName"])
                    if not target:
                        raise FileNotFoundError("Export not found")
                    return self._send(200, condition_copy(target))
                if parts[3] == "news":
                    catalog = condition.read_catalog(CONDITIONS, CONDITION_STATE, area)
                    query = catalog["area"].get("newsQuery") or catalog["area"]["name"]
                    try:
                        return self._send(200, news.refresh(CONDITION_STATE / catalog["area"]["id"] / "news.json", query))
                    except news.NewsUnavailable as e:
                        return self._send(503, {"error": str(e), **news.cached(CONDITION_STATE / catalog["area"]["id"] / "news.json")})
                if parts[3] == "radar":
                    condition.read_catalog(CONDITIONS, CONDITION_STATE, area)  # the area must exist
                    orbit = params.get("orbit") if isinstance(params, dict) else None
                    job = Job({"task": "radar", "area": area, "orbit": int(orbit) if orbit else None})
                    with JOBS_LOCK:
                        JOBS[job.id] = job
                    threading.Thread(target=run_radar_job, args=(job,), daemon=True).start()
                    return self._send(200, {"id": job.id})
                return self._send(404, {"error": "Not found"})
            except FileNotFoundError:
                return self._send(404, {"error": "Catalog or export not found"})
            except (ValueError, TypeError, KeyError) as e:
                return self._send(400, {"error": str(e)})

        if parts == ["api", "jobs"]:
            try:
                params = json.loads(raw)
                if len(params.get("polygon") or []) < 3:
                    raise ValueError("polygon needs at least 3 points")
            except (ValueError, TypeError) as e:
                return self._send(400, {"error": str(e)})
            job = Job(params)
            with JOBS_LOCK:
                JOBS[job.id] = job
                for old in sorted(JOBS.values(), key=lambda j: j.created)[:-MAX_JOBS]:
                    if old.status != "running":
                        del JOBS[old.id]
            threading.Thread(target=run_job, args=(job,), daemon=True).start()
            return self._send(200, {"id": job.id})

        if parts[:2] == ["api", "jobs"] and len(parts) == 4 and parts[3] == "cancel":
            job = self._job(parts[2])
            if not job:
                return self._send(404, {"error": "unknown job"})
            # The worker may be blocked in a network read; free the UI now and
            # let the worker discard its result whenever it wakes up.
            job.cancelled = True
            if job.status == "running":
                job.status = "cancelled"
                job.log.append(f"{time.strftime('%H:%M:%S')}  Cancelled")
            return self._send(200, {"ok": True})

        self._send(404, {"error": "not found"})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--open", action="store_true", help="open the app in the default browser")
    args = ap.parse_args()

    CACHE.mkdir(exist_ok=True)
    httpd = LocalServer(("127.0.0.1", args.port), Handler)
    url = f"http://localhost:{args.port}/"
    print(f"Building Extractor running at {url}  (Ctrl+C to stop)")
    if args.open:
        threading.Timer(0.5, lambda: webbrowser.open(url)).start()
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
