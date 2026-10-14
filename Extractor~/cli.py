"""Command-line extraction, for scripting or areas too big for the browser.

Examples:
  py -3 cli.py --bbox 49.82,36.66,49.85,36.71 --out chuhuiv.json
  py -3 cli.py --polygon area.geojson --sources osm --origin 49.8335,36.6866
  py -3 cli.py --bbox 49.82,36.66,49.85,36.71 --out chuhuiv.json --environment chuhuiv_terrain
  py -3 cli.py --bbox 49.82,36.66,49.85,36.71 --name chuhuiv     (into the app's exports, as the app names them)

--machine prints JSON lines on stdout for a program that runs the extraction (the Geo World Unity package):
{"progress": 0.42, "stage": "..."}, {"log": "..."}, then {"done": {"export": path, "package": folder}} or
{"error": "..."}. BUILDING_EXTRACTOR_HOME moves the cache and the exports folder out of the app's own folder.

The building condition (war damage) comes with the export as in the app: the radar estimate of the area
(made the first time, reused for a week) and the evidence of the area that covers it; --no-radar skips the
estimate and uses only what the area already has.
"""
import argparse
import json
import sys
import traceback
from pathlib import Path

from extractor import condition, package, world

ROOT = Path(__file__).resolve().parent
HOME = package.home(ROOT)
CONDITIONS, CONDITION_STATE = ROOT / "data" / "condition", HOME / "cache" / "condition"


def _read_polygon(path):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if data.get("type") == "FeatureCollection":
        data = data["features"][0]
    if data.get("type") == "Feature":
        data = data["geometry"]
    if data["type"] == "MultiPolygon":
        coords = data["coordinates"][0][0]
    elif data["type"] == "Polygon":
        coords = data["coordinates"][0]
    else:
        raise SystemExit("GeoJSON must contain a Polygon")
    return [(lat, lon) for lon, lat in coords]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    area = ap.add_mutually_exclusive_group(required=True)
    area.add_argument("--bbox", help="south,west,north,east in degrees")
    area.add_argument("--polygon", help="GeoJSON file with a Polygon (lon/lat)")
    ap.add_argument("--sources", default="osm,microsoft", help="comma list: osm,microsoft")
    ap.add_argument("--origin", help="lat,lon of the Unity world origin (default: area centre)")
    ap.add_argument("--min-area", type=float, default=6.0, help="drop footprints smaller than this (m2)")
    ap.add_argument("--no-elevation", action="store_true", help="skip the ground elevation lookup")
    ap.add_argument("--no-roof-estimates", action="store_true",
                    help="keep only roof data that is mapped in OSM or reviewed; do not fill the rest with typical values")
    ap.add_argument("--keep-conflicts", action="store_true",
                    help="keep outlines that overlap each other or stand on a road as their source has them")
    ap.add_argument("--environment", metavar="DIR",
                    help="also build the terrain and ground package (heightmap, material map, roads, water, "
                         "trees) into this folder, plus DIR.zip")
    ap.add_argument("--no-heightmap", action="store_true", help="with --environment: skip the terrain heightmap")
    ap.add_argument("--no-ground", action="store_true", help="with --environment: skip the material map and map features")
    ap.add_argument("--height-cell", type=float, default=4.0, help="metres between heightmap samples (default 4)")
    ap.add_argument("--ground-cell", type=float, default=1.0, help="metres per material map pixel (default 1)")
    ap.add_argument("--no-radar", action="store_true",
                    help="no radar estimate of the damage: only the condition the area already has")
    output = ap.add_mutually_exclusive_group(required=True)
    output.add_argument("--out", help="output JSON path")
    output.add_argument("--name", help="save into the exports folder as the app does (<name>_<lat>N_<lon>E_<date>.json), "
                                       "with its terrain and ground package beside it")
    ap.add_argument("--machine", action="store_true", help="progress and result as JSON lines on stdout")
    args = ap.parse_args()
    if not args.machine:
        return run(args)
    try:
        run(args)
    except BaseException as e:  # a program reads stdout: say what went wrong there, then fail
        traceback.print_exc()
        emit({"error": str(e) or type(e).__name__})
        sys.exit(1)


def emit(message):
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


def run(args):

    if args.bbox:
        s, w, n, e = (float(v) for v in args.bbox.split(","))
        polygon = [(s, w), (n, w), (n, e), (s, e)]
    else:
        polygon = _read_polygon(args.polygon)
    origin = tuple(float(v) for v in args.origin.split(",")) if args.origin else None
    sources = [s.strip() for s in args.sources.split(",") if s.strip()]

    if args.name:
        args.environment = True
    radar = not args.no_radar

    def progress(frac, msg=None, start=0.0, span=1.0):
        if args.machine:
            emit({"progress": None if frac is None else round(start + span * frac, 4), "stage": msg})
        elif msg:
            sys.stderr.write(f"\r{msg[:100]:<100}")
            sys.stderr.flush()

    def log(msg):
        if args.machine:
            emit({"log": msg})
        else:
            sys.stderr.write(f"\r{'':<100}\r{msg}\n")

    # with a radar estimate the extraction is the first 60% of the way
    extract_progress = (lambda frac, msg=None: progress(frac, msg, 0.0, 0.6)) if radar else progress
    env_options = None
    if args.environment:
        env_options = {"heightmap": not args.no_heightmap, "ground": not args.no_ground,
                       "heightCell": args.height_cell, "groundCell": args.ground_cell}
    result = world.extract(polygon, sources, HOME / "cache", origin=origin, min_area=args.min_area,
                           with_elevation=not args.no_elevation, env_options=env_options,
                           estimate_roofs=not args.no_roof_estimates, fix_conflicts=not args.keep_conflicts,
                           log=log, progress=extract_progress)
    export = result.export
    if args.name:
        exports = HOME / "exports"
        exports.mkdir(parents=True, exist_ok=True)
        stem = package.export_stem(args.name, export["origin"])
        args.out, args.environment = str(exports / (stem + ".json")), str(exports / (stem + "_env"))
    catalog = None
    if radar:
        try:
            catalog = condition.estimate_radar(CONDITIONS, CONDITION_STATE, HOME / "cache", export, args.name or Path(args.out).stem,
                                               log=log, progress=lambda frac, msg=None: progress(frac, msg, 0.6, 0.4))
        except (OSError, RuntimeError, ValueError) as e:  # the buildings are still worth saving
            log(f"Radar estimate failed: {e}")
            export.setdefault("warnings", []).append(f"No radar estimate of the damage: {e}")
    catalog = catalog or condition.find_catalog(CONDITIONS, CONDITION_STATE, export)
    if catalog:
        rasters = condition.load_rasters(catalog)
        log("Building condition: " + condition.enrich(export, result.env.json if result.env else None, catalog, rasters)["message"])
        if result.env:
            result.env.files.update(condition.package_files(catalog, export, rasters))
    Path(args.out).write_text(json.dumps(export, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    log(f"Saved {len(export['buildings'])} buildings to {args.out}")
    if result.env:
        folder = Path(args.environment)
        package.write(folder, export, result.env)
        Path(f"{folder}.zip").write_bytes(package.zip_bytes(folder))
        log(f"Saved the terrain and ground package to {folder} and {folder}.zip")
    if args.machine:
        emit({"done": {"export": str(Path(args.out).resolve()), "package": str(Path(args.environment).resolve()) if result.env else None,
                       "buildings": len(export["buildings"])}})


if __name__ == "__main__":
    main()
