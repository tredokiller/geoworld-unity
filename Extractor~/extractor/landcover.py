"""Land cover from ESA WorldCover 2021 (10 m, global, CC BY 4.0).

Each pixel is one of eleven classes (tree cover, grassland, cropland, built-up, water, ...).
It is the base layer of the material map; OpenStreetMap shapes are painted on top.
"""
import math
from itertools import accumulate
from operator import add, itemgetter, sub

from .cog import Cog, TileMissing

NAME = "ESA WorldCover 2021"
ATTRIBUTION = "ESA WorldCover 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium, CC BY 4.0"
URL = ("https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/"
       "ESA_WorldCover_10m_2021_v200_{ns}{lat:02d}{ew}{lon:03d}_Map.tif")
TILE_DEG = 3
WATER = 80
TREES, GRASS, BUILT = 10, 30, 50
GARDEN_RADIUS_PX = 5      # 10 m pixels: trees within 50 m of buildings are garden / street trees
GARDEN_BUILT_SHARE = 0.2  # share of built-up pixels around a tree pixel that makes it a garden tree


def _url(lat, lon):
    return URL.format(ns="N" if lat >= 0 else "S", lat=abs(lat), ew="E" if lon >= 0 else "W", lon=abs(lon))


class WorldCover:
    """The WorldCover pixels around an area, stitched into one block."""

    def __init__(self, south, west, north, east, cache_dir, log, progress, is_cancelled, margin_px=3):
        probe = None
        self.sx = self.sy = None
        tiles = {}
        for la in range(math.floor(south / TILE_DEG) * TILE_DEG, math.floor(north / TILE_DEG) * TILE_DEG + 1, TILE_DEG):
            for lo in range(math.floor(west / TILE_DEG) * TILE_DEG, math.floor(east / TILE_DEG) * TILE_DEG + 1, TILE_DEG):
                try:
                    tiles[(la, lo)] = Cog(_url(la, lo), cache_dir)
                    probe = probe or tiles[(la, lo)]
                except TileMissing:
                    tiles[(la, lo)] = None  # open sea
        if probe is None:
            raise RuntimeError("No land cover data exists for this area")
        # Every tile shares one pixel size and a grid aligned to whole degrees.
        self.sx, self.sy = probe.sx, probe.sy
        self.gx0 = math.floor((west + 180.0) / self.sx) - margin_px
        self.gx1 = math.ceil((east + 180.0) / self.sx) + margin_px
        self.gy0 = math.floor((90.0 - north) / self.sy) - margin_px
        self.gy1 = math.ceil((90.0 - south) / self.sy) + margin_px
        self.w, self.h = self.gx1 - self.gx0, self.gy1 - self.gy0
        if self.w * self.h > 400_000_000:
            raise ValueError("The selection is too large for the land cover data")
        self.data = bytearray([WATER]) * (self.w * self.h)

        total = sum(c.missing_bytes(*self._local(c, la, lo)) for (la, lo), c in tiles.items() if c)
        if total:
            log(f"Land cover: downloading {total / 1e6:.2f} MB")
        got = [0]

        def report(n):
            got[0] += n
            progress(min(1.0, got[0] / total) if total else None, f"Land cover: downloaded {got[0] / 1e6:.2f} of {total / 1e6:.2f} MB")

        for (la, lo), cog in tiles.items():
            if cog is None:
                continue
            c0, r0, c1, r1 = self._local(cog, la, lo)
            arr, wc0, wr0, w, h = cog.read_window(c0, r0, c1, r1, is_cancelled=is_cancelled, report=report)
            ox = round((lo + 180.0) / self.sx) + wc0 - self.gx0
            oy = round((90.0 - (la + TILE_DEG)) / self.sy) + wr0 - self.gy0
            raw = arr.tobytes()
            for r in range(h):
                y = oy + r
                if 0 <= y < self.h:
                    a = max(0, -ox)
                    b = min(w, self.w - ox)
                    if b > a:
                        self.data[y * self.w + ox + a:y * self.w + ox + b] = raw[r * w + a:r * w + b]
        self.ground = _garden_trees_to_grass(self.data, self.w, self.h)
        log(f"Land cover: {NAME}, {self.w} x {self.h} px block")

    def _local(self, cog, la, lo):
        """Pixel window of the area inside one tile, in that tile's own coordinates."""
        ox = round((lo + 180.0) / self.sx)
        oy = round((90.0 - (la + TILE_DEG)) / self.sy)
        return self.gx0 - ox, self.gy0 - oy, self.gx1 - ox, self.gy1 - oy

    def render(self, proj, min_x, max_z, cell, width, height, lut, ground=True, smooth=True):
        """Material ids for a local grid (see raster.Canvas).

        `lut` is a 256-byte table from WorldCover class code to material id. With ground=True,
        tree cover among buildings counts as grass; ground=False uses the raw classes.

        Each grid pixel takes the class with the most weight among the four WorldCover pixels around
        it (bilinear weights) when smooth=True, which rounds the 10 m staircase edges; otherwise the
        nearest pixel. Rows and columns are located at the middle of the grid, which is off by well
        under a WorldCover pixel for areas up to about 10 km across.
        """
        xc = min_x + width * cell / 2.0
        zc = max_z - height * cell / 2.0
        wmax, hmax = self.w - 1, self.h - 1
        fxs = []
        for i in range(width):
            lon = proj.inverse(min_x + (i + 0.5) * cell, zc)[1]
            fxs.append(min(max((lon + 180.0) / self.sx - self.gx0 - 0.5, 0.0), wmax))
        i0 = [min(int(f), max(wmax - 1, 0)) for f in fxs]
        i1 = [min(i + 1, wmax) for i in i0]
        u1 = [f - i for f, i in zip(fxs, i0)]
        u0 = [1.0 - u for u in u1]
        pick0 = itemgetter(*i0) if width > 1 else (lambda row: (row[i0[0]],))
        pick1 = itemgetter(*i1) if width > 1 else (lambda row: (row[i1[0]],))

        source = self.ground if ground else self.data
        rows = {}

        def translated(gy):
            row = rows.get(gy)
            if row is None:
                row = rows[gy] = bytes(source[gy * self.w:(gy + 1) * self.w]).translate(lut)
                if len(rows) > 6:
                    rows.pop(next(iter(rows)))
            return row

        out = bytearray(width * height)
        for j in range(height):
            lat = proj.inverse(xc, max_z - (j + 0.5) * cell)[0]
            fy = min(max((90.0 - lat) / self.sy - self.gy0 - 0.5, 0.0), hmax)
            j0 = min(int(fy), max(hmax - 1, 0))
            r0 = translated(j0)
            if not smooth:
                row_out = bytes(pick0(translated(j0 + 1) if fy - j0 >= 0.5 and j0 < hmax else r0))
            else:
                r1 = translated(min(j0 + 1, hmax))
                v1 = fy - j0
                v0 = 1.0 - v1
                row_out = bytes([
                    a if a == b == c == d else _vote(a, b, c, d, v0 * p, v0 * q, v1 * p, v1 * q)
                    for a, b, c, d, p, q in zip(pick0(r0), pick1(r0), pick0(r1), pick1(r1), u0, u1)])
            out[j * width:(j + 1) * width] = row_out
        return out

    def class_at(self, lat, lon):
        x = int((lon + 180.0) / self.sx) - self.gx0
        y = int((90.0 - lat) / self.sy) - self.gy0
        if 0 <= x < self.w and 0 <= y < self.h:
            return self.data[y * self.w + x]
        return 0


def _vote(a, b, c, d, wa, wb, wc, wd):
    """The class with the largest total weight among four corner classes."""
    total = {a: wa}
    total[b] = total.get(b, 0.0) + wb
    total[c] = total.get(c, 0.0) + wc
    total[d] = total.get(d, 0.0) + wd
    return max(total, key=total.get)


_BUILT_ONLY = bytes(1 if c == BUILT else 0 for c in range(256))


def _garden_trees_to_grass(data, w, h):
    """Copy of the land cover where tree cover among buildings becomes grassland.

    WorldCover calls every garden and street tree "tree cover". As a ground material that
    would put forest floor under back yards, so those pixels count as grass instead; the
    trees themselves are still known from the raw classes.
    """
    built = bytes(data).translate(_BUILT_ONLY)
    r = GARDEN_RADIUS_PX
    # Summed-area table of built-up pixels: row j holds sums over rows above j.
    table = [[0] * (w + 1)]
    for j in range(h):
        acc = [0]
        acc.extend(accumulate(built[j * w:(j + 1) * w]))
        table.append(list(map(add, table[-1], acc)))
    x0s = [max(0, x - r) for x in range(w)]
    x1s = [min(w, x + r + 1) for x in range(w)]
    widths = [b - a for a, b in zip(x0s, x1s)]
    get0 = itemgetter(*x0s) if w > 1 else (lambda row: (row[x0s[0]],))
    get1 = itemgetter(*x1s) if w > 1 else (lambda row: (row[x1s[0]],))
    out = bytearray(data)
    for j in range(h):
        row = out[j * w:(j + 1) * w]
        if TREES not in row:
            continue
        top, bottom = table[max(0, j - r)], table[min(h, j + r + 1)]
        rows_n = min(h, j + r + 1) - max(0, j - r)
        sums = map(sub, map(add, get1(bottom), get0(top)), map(add, get0(bottom), get1(top)))
        changed = False
        for x, (s, wd) in enumerate(zip(sums, widths)):
            if row[x] == TREES and s >= GARDEN_BUILT_SHARE * wd * rows_n:
                row[x] = GRASS
                changed = True
        if changed:
            out[j * w:(j + 1) * w] = row
    return out
