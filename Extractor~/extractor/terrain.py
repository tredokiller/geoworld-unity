"""Terrain: ground elevation and heightmaps from the Copernicus GLO-30 elevation model.

The model is a 1 arc-second (about 30 m) grid, published as one GeoTIFF per
degree square. Only the parts of those files that cover the selection are
downloaded, and they are cached. It is a surface model: it reads a little high
over forests and dense buildings.
"""
import math
import sys
from array import array

from .cog import Cog, TileMissing
from .common import Cancelled

NAME = "Copernicus GLO-30"
ATTRIBUTION = ("Copernicus DEM GLO-30 (c) DLR e.V. 2010-2014 and (c) Airbus Defence and Space GmbH 2014-2018, "
               "provided under COPERNICUS by the European Union and ESA; all rights reserved")
URL = ("https://copernicus-dem-30m.s3.amazonaws.com/Copernicus_DSM_COG_10_{ns}{lat:02d}_00_{ew}{lon:03d}_00_DEM/"
       "Copernicus_DSM_COG_10_{ns}{lat:02d}_00_{ew}{lon:03d}_00_DEM.tif")
MARGIN_PX = 4


def _tile_url(lat, lon):
    return URL.format(ns="N" if lat >= 0 else "S", lat=abs(lat), ew="E" if lon >= 0 else "W", lon=abs(lon))


class _Patch:
    """The part of one DEM file that covers the area, with a fast bilinear sampler."""

    def __init__(self, cog, window):
        self.data, self.col0, self.row0, self.w, self.h = window
        self.cog = cog

    def sampler(self):
        cog, data, w, h = self.cog, self.data, self.w, self.h
        x0, y0 = cog.x0, cog.y0
        isx, isy = 1.0 / cog.sx, 1.0 / cog.sy
        half = 0.0 if cog.pixel_is_point else 0.5  # index space has integers at pixel centres
        ox, oy = self.col0 + half, self.row0 + half
        wm1, hm1 = w - 1, h - 1

        def sample(lat, lon):
            fx = (lon - x0) * isx - ox
            fy = (y0 - lat) * isy - oy
            if fx < 0.0:
                fx = 0.0
            elif fx > wm1:
                fx = wm1
            if fy < 0.0:
                fy = 0.0
            elif fy > hm1:
                fy = hm1
            ix, iy = int(fx), int(fy)
            if ix >= wm1:
                ix = wm1 - 1
            if iy >= hm1:
                iy = hm1 - 1
            tx, ty = fx - ix, fy - iy
            i = iy * w + ix
            a, b, c, d = data[i], data[i + 1], data[i + w], data[i + w + 1]
            return (a + (b - a) * tx) * (1.0 - ty) + (c + (d - c) * tx) * ty
        return sample


class Dem:
    """Elevation (metres above sea level) over a lat/lon box, as one seamless grid.

    The model comes in one file per degree square, and north of 50 degrees the files have a
    coarser east-west spacing. So the pieces are resampled onto a single grid of one arc-second,
    placed on whole arc-seconds, which makes the tile edges invisible.
    """

    MAX_NODES = 12_000_000

    def __init__(self, south, west, north, east, cache_dir, log, progress, is_cancelled):
        margin = 3
        self.i0 = math.floor(west * 3600) - margin   # grid column of longitude i / 3600
        i1 = math.ceil(east * 3600) + margin
        j0 = math.floor(south * 3600) - margin
        self.j1 = math.ceil(north * 3600) + margin   # grid row 0 is latitude j1 / 3600
        self.w, self.h = i1 - self.i0 + 1, self.j1 - j0 + 1
        if self.w * self.h > self.MAX_NODES:
            raise ValueError("The selection is too large for the terrain model")

        # A posting on a whole degree belongs to the tile below / east of it (see the file layout).
        pieces = []  # (tile key, first/last node column, first/last node row)
        for la in range((j0 - 1) // 3600, (self.j1 - 1) // 3600 + 1):
            for lo in range(self.i0 // 3600, i1 // 3600 + 1):
                ja, jb = max(j0, la * 3600 + 1), min(self.j1, (la + 1) * 3600)
                ia, ib = max(self.i0, lo * 3600), min(i1, (lo + 1) * 3600 - 1)
                if ja <= jb and ia <= ib:
                    pieces.append(((la, lo), ia, ib, ja, jb))
        if len(pieces) > 25:
            raise ValueError("The selection is too large for the terrain model")

        cogs = {}
        for key, *_ in pieces:
            try:
                cogs[key] = Cog(_tile_url(*key), cache_dir)
            except TileMissing:
                pass  # no file: open sea, which stays at 0
        if not cogs:
            raise RuntimeError("No terrain data exists for this area")

        windows = {}
        for key, ia, ib, ja, jb in pieces:
            cog = cogs.get(key)
            if cog is None:
                continue
            c0 = int(math.floor(cog.col_of(ia / 3600))) - MARGIN_PX
            c1 = int(math.ceil(cog.col_of(ib / 3600))) + MARGIN_PX + 1
            r0 = int(math.floor(cog.row_of(jb / 3600))) - MARGIN_PX
            r1 = int(math.ceil(cog.row_of(ja / 3600))) + MARGIN_PX + 1
            windows[key] = (c0, r0, c1, r1)
        total = sum(cogs[key].missing_bytes(*w) for key, w in windows.items())
        if total:
            log(f"Terrain: downloading {total / 1e6:.1f} MB of elevation data (cached afterwards)")
        got = [0]

        def report(n):
            got[0] += n
            progress(min(1.0, got[0] / total) if total else None,
                     f"Terrain: downloaded {got[0] / 1e6:.1f} of {total / 1e6:.1f} MB")

        self.data = array("f", bytes(4 * self.w * self.h))
        for key, ia, ib, ja, jb in pieces:
            if key not in windows:
                continue
            patch = _Patch(cogs[key], cogs[key].read_window(*windows[key], is_cancelled=is_cancelled, report=report))
            if not (patch.w and patch.h):
                continue
            fn = patch.sampler()
            lons = [i / 3600 for i in range(ia, ib + 1)]
            for j in range(ja, jb + 1):
                if j % 64 == 0 and is_cancelled():
                    raise Cancelled()
                lat = j / 3600
                base = (self.j1 - j) * self.w + ia - self.i0
                self.data[base:base + len(lons)] = array("f", [fn(lat, lon) for lon in lons])
        log(f"Terrain: {NAME}, {len(cogs)} file(s), {self.w} x {self.h} grid")

    def sampler(self):
        """Returns f(lat, lon) -> metres above sea level (0 over open sea)."""
        data, w = self.data, self.w
        i0, j1 = self.i0, self.j1
        wm1, hm1 = w - 1, self.h - 1

        def sample(lat, lon):
            fx = lon * 3600.0 - i0
            fy = j1 - lat * 3600.0
            if fx < 0.0:
                fx = 0.0
            elif fx > wm1:
                fx = wm1
            if fy < 0.0:
                fy = 0.0
            elif fy > hm1:
                fy = hm1
            ix, iy = int(fx), int(fy)
            if ix >= wm1:
                ix = wm1 - 1
            if iy >= hm1:
                iy = hm1 - 1
            tx, ty = fx - ix, fy - iy
            i = iy * w + ix
            a, b, c, d = data[i], data[i + 1], data[i + w], data[i + w + 1]
            return (a + (b - a) * tx) * (1.0 - ty) + (c + (d - c) * tx) * ty
        return sample


# ---------------------------------------------------------------- heightmap

class Heightmap:
    """Regular grid of ground heights in local metres.

    Sample (i, j) sits at x = min_x + i * cell and z = max_z - j * cell, so row 0
    is the northern edge. `data` holds metres above sea level.
    """

    def __init__(self, width, height, cell, min_x, max_z, data):
        self.width, self.height, self.cell = width, height, cell
        self.min_x, self.max_z = min_x, max_z
        self.data = data
        self.min_h, self.max_h = min(data), max(data)

    def to_r16(self):
        """Little-endian unsigned 16-bit samples scaled so min_h -> 0 and max_h -> 65535."""
        lo, span = self.min_h, (self.max_h - self.min_h) or 1.0
        k = 65535.0 / span
        out = array("H", [int((v - lo) * k + 0.5) for v in self.data])
        if sys.byteorder == "big":
            out.byteswap()
        return out.tobytes()

    def at(self, x, z):
        """Bilinear height at a local point (clamped to the grid)."""
        fx = min(max((x - self.min_x) / self.cell, 0.0), self.width - 1.0)
        fy = min(max((self.max_z - z) / self.cell, 0.0), self.height - 1.0)
        ix, iy = min(int(fx), max(self.width - 2, 0)), min(int(fy), max(self.height - 2, 0))
        tx, ty = fx - ix, fy - iy
        w = self.width
        i = iy * w + ix
        d = self.data
        j = i + 1 if ix + 1 < w else i
        k = i + w if iy + 1 < self.height else i
        m = k + 1 if ix + 1 < w else k
        return (d[i] + (d[j] - d[i]) * tx) * (1 - ty) + (d[k] + (d[m] - d[k]) * tx) * ty


def grid_for(bounds, cell, max_side=2049):
    """Snaps the bounds to multiples of `cell` from the origin. Returns (cell, min_x, max_z, width, height).

    Snapping makes exports that share an origin and a cell size line up sample for sample.
    """
    min_x0, min_z0, max_x0, max_z0 = bounds
    while True:
        min_x = math.floor(min_x0 / cell) * cell
        max_z = math.ceil(max_z0 / cell) * cell
        width = int(math.ceil((max_x0 - min_x) / cell)) + 1
        height = int(math.ceil((max_z - min_z0) / cell)) + 1
        if max(width, height) <= max_side:
            return cell, min_x, max_z, width, height
        cell *= 2


def iter_latlon_rows(proj, cell, min_x, max_z, width, height, stride=32):
    """Yields (lats, lons) for each grid row, north to south.

    Exact projection inverses are computed on a coarse lattice and interpolated in between,
    which is accurate to well under a millimetre and far faster than inverting every sample.
    """
    cols = list(range(0, width, stride))
    if cols[-1] != width - 1:
        cols.append(width - 1)
    rows = list(range(0, height, stride))
    if rows[-1] != height - 1:
        rows.append(height - 1)
    nodes = []
    for rj in rows:
        z = max_z - rj * cell
        nodes.append([proj.inverse(min_x + ci * cell, z) for ci in cols])
    if len(rows) == 1:
        rows = rows * 2
        nodes = nodes * 2
    a = 0
    for j in range(height):
        while a + 2 < len(rows) and j > rows[a + 1]:
            a += 1
        span = (rows[a + 1] - rows[a]) or 1
        t = (j - rows[a]) / span
        top, bot = nodes[a], nodes[a + 1]
        nlat = [p[0] + (q[0] - p[0]) * t for p, q in zip(top, bot)]
        nlon = [p[1] + (q[1] - p[1]) * t for p, q in zip(top, bot)]
        lats, lons = [], []
        for b in range(len(cols) - 1):
            n = cols[b + 1] - cols[b]
            dl, dn = (nlat[b + 1] - nlat[b]) / n, (nlon[b + 1] - nlon[b]) / n
            lats.extend([nlat[b] + dl * k for k in range(n)])
            lons.extend([nlon[b] + dn * k for k in range(n)])
        lats.append(nlat[-1])
        lons.append(nlon[-1])
        if len(cols) == 1:
            lats, lons = [nlat[0]], [nlon[0]]
        yield lats, lons


def build_heightmap(dem, proj, bounds, cell, progress, is_cancelled, max_side=2049):
    """Samples the DEM on a regular local grid covering `bounds` = (min_x, min_z, max_x, max_z)."""
    cell, min_x, max_z, width, height = grid_for(bounds, cell, max_side)
    sample = dem.sampler()
    data = array("f")
    for j, (lats, lons) in enumerate(iter_latlon_rows(proj, cell, min_x, max_z, width, height)):
        if j % 32 == 0:
            if is_cancelled():
                raise Cancelled()
            progress(j / height, f"Terrain: heightmap row {j}/{height}")
        data.extend([sample(la, lo) for la, lo in zip(lats, lons)])
    return Heightmap(width, height, cell, min_x, max_z, data)


# ---------------------------------------------------------------- preview

def hillshade(hm, max_side=1024):
    """Shaded-relief picture of a heightmap: returns (width, height, rgba bytes), light from the north-west."""
    step = max(1, math.ceil(max(hm.width, hm.height) / max_side))
    w, h = (hm.width - 1) // step + 1, (hm.height - 1) // step + 1
    d, W = hm.data, hm.width
    lo, hi = hm.min_h, hm.max_h
    span = (hi - lo) or 1.0
    lx, ly, lz = -0.5, 0.5, 0.707  # towards the light; +y is north
    out = bytearray(w * h * 4)
    for j in range(h):
        jj = j * step
        jn, js = max(jj - step, 0), min(jj + step, hm.height - 1)  # north and south neighbours
        mid = jj * W
        for i in range(w):
            ii = i * step
            le, ri = max(ii - step, 0), min(ii + step, W - 1)
            dzdx = (d[mid + ri] - d[mid + le]) / (max(ri - le, 1) * hm.cell)
            dzdy = (d[jn * W + ii] - d[js * W + ii]) / (max(js - jn, 1) * hm.cell)
            nrm = math.sqrt(dzdx * dzdx + dzdy * dzdy + 1.0)
            shade = (-dzdx * lx - dzdy * ly + lz) / nrm
            shade = min(max(shade, 0.0), 1.0)
            t = (d[mid + ii] - lo) / span
            # low = green-brown, high = light grey
            r = 70 + 150 * t
            g = 110 + 100 * t
            b = 60 + 130 * t
            k = (j * w + i) * 4
            s = 0.35 + 0.65 * shade
            out[k] = int(min(255, r * s * 1.15))
            out[k + 1] = int(min(255, g * s * 1.15))
            out[k + 2] = int(min(255, b * s * 1.15))
            out[k + 3] = 255
    return w, h, bytes(out)
