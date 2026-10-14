"""Writes and zips the terrain and ground package that goes with a buildings export."""
import io
import json
import os
import re
import time
import zipfile
from pathlib import Path


def home(root):
    """Where the app keeps its cache and exports: BUILDING_EXTRACTOR_HOME when set (the app inside the Unity package
    is read-only, so Unity points it at a folder of the user's), else beside the app (root)."""
    return Path(os.environ.get("BUILDING_EXTRACTOR_HOME") or root)


def export_stem(name, origin):
    """The file name (without .json) of an export: <name>_<lat>N_<lon>E_<date-time>, name made file-safe."""
    slug = re.sub(r"[^A-Za-z0-9_-]+", "_", name or "").strip("_")[:60] or "buildings"
    return f"{slug}_{origin['lat']:.4f}N_{origin['lon']:.4f}E_{time.strftime('%Y%m%d-%H%M%S')}"

README = """Terrain and ground package
==========================

buildings.json     Buildings (format geo-buildings), unchanged.
environment.json   Terrain, roads, water, fields, trees, mapped street props and barriers (geo-environment).
heightmap.r16      Ground heights, 16-bit unsigned little-endian, no header. Rows run north to south.
                   metres above sea level = minHeight + value / 65535 * (maxHeight - minHeight)
                   (the numbers are in environment.json under terrain.heightmap)
materials.png      Ground material per pixel, 8-bit greyscale: the pixel value is the material id.
                   Ids and names are listed in environment.json under materials.classes.
canopy.png         255 where trees stand, 0 elsewhere. Same grid as materials.png.
street-references.json, reference-*.jpg, STREET-REFERENCE-CREDITS.txt (when present)
                   Historical visual evidence, dates, licenses and reviewed observations.
                   Only explicitly linked buildings or separately placed props are applied.
                   Unlinked observations are style references, not surveyed object positions.
condition.json, CONDITION-CREDITS.txt, sar-*-preview.png (when present)
                   Building condition (war damage): buildings.json carries "condition" on every building
                   that has evidence: state (intact, damaged, heavily_damaged, destroyed), basis (observed,
                   reported, mapped, estimated), confidence, the date it applies to, and the source IDs.
                   A building without "condition" is unknown. "estimated" comes from satellite radar and is
                   a low-confidence estimate; condition.json lists the sources, their dates and licenses.
roof (in buildings.json)
                   Optional block on every building with roof data: shape, ridge direction, pitch, rise,
                   material and colour, each with a Source: "mapped" (OpenStreetMap tag), "reviewed" (a person
                   checked imagery) or "estimated" (typical for this kind of building, low confidence, not an
                   observation). Ignore "estimated" fields to show only facts. See roofInfo for counts.
props / barriers   Optional arrays in environment.json: mapped objects and fence/wall lines.
                   Missing dimensions are unknown; entrances are access-clearance markers.
                   Individual trees scattered from canopy/tree rows are generated placements.
materials_preview.png, hillshade.png
                   Coloured pictures of the two maps above, for looking at.

All positions are metres on a local east / north plane around the origin in
environment.json (x = east, z = north), the same as the buildings.
Pixel (i, j) of a map covers x from minX + i * cellSize and z from maxZ - j * cellSize
downwards, so the top row of every picture is the northern edge.
For a Unity Terrain, import heightmap.r16 as RAW, 16 bit, Windows byte order.
Y = metres above sea level minus terrain.heightmap.originGround puts the ground at the same
height as the buildings' "y" values.

Data: OpenStreetMap contributors (ODbL); ESA WorldCover 2021 (CC BY 4.0); Copernicus DEM GLO-30.
"""


def write(folder, export, env):
    """Writes every file of an environment.Result, plus buildings.json, into `folder`."""
    folder.mkdir(parents=True, exist_ok=True)
    for name, data in env.files.items():
        (folder / name).write_bytes(data)
    (folder / "buildings.json").write_bytes(_json(export))
    (folder / "environment.json").write_bytes(_json(env.json))
    (folder / "README.txt").write_text(README, encoding="utf-8")


def zip_bytes(folder):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(folder.iterdir()):
            if path.is_file():
                z.write(path, path.name)
    return buf.getvalue()


def _json(obj):
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
