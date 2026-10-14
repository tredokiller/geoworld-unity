"""Small raster toolkit (standard library only): PNG writing and a paintable class canvas.

Grids are in local metres: x grows east, z grows north. Row 0 of every image is
the northern edge, so images look like a map.
"""
import math
import struct
import sys
import zlib
from array import array


# ---------------------------------------------------------------- PNG

def _chunk(kind, payload):
    body = kind + payload
    return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def _png(width, height, color_type, bit_depth, rows, extra_chunks=()):
    """`rows` yields the raw bytes of each scanline (without the filter byte)."""
    comp = zlib.compressobj(6)
    parts = []
    for row in rows:
        parts.append(comp.compress(b"\x00" + bytes(row)))
    parts.append(comp.flush())
    out = [b"\x89PNG\r\n\x1a\n",
           _chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, bit_depth, color_type, 0, 0, 0))]
    out.extend(extra_chunks)
    out.append(_chunk(b"IDAT", b"".join(parts)))
    out.append(_chunk(b"IEND", b""))
    return b"".join(out)


def _rows(data, width, height, bytes_per_pixel):
    stride = width * bytes_per_pixel
    mv = memoryview(data)
    return (mv[y * stride:(y + 1) * stride] for y in range(height))


def png_gray8(width, height, data):
    """8-bit greyscale; `data` holds width * height bytes."""
    return _png(width, height, 0, 8, _rows(data, width, height, 1))


def png_rgb(width, height, data):
    return _png(width, height, 2, 8, _rows(data, width, height, 3))


def png_rgba(width, height, data):
    return _png(width, height, 6, 8, _rows(data, width, height, 4))


def png_indexed(width, height, data, palette, alpha=None):
    """8-bit paletted image. palette: [(r, g, b), ...]; alpha: optional [0..255, ...] per entry."""
    extra = [_chunk(b"PLTE", bytes(c for rgb in palette for c in rgb))]
    if alpha:
        extra.append(_chunk(b"tRNS", bytes(alpha)))
    return _png(width, height, 3, 8, _rows(data, width, height, 1), extra)


def png_gray16(width, height, values):
    """16-bit greyscale; `values` is an array('H') in host byte order."""
    big_endian = array("H", values)
    if sys.byteorder == "little":
        big_endian.byteswap()
    return _png(width, height, 0, 16, _rows(big_endian.tobytes(), width, height, 2))


# ---------------------------------------------------------------- class canvas

class Canvas:
    """A grid of byte values that polygons and thick lines can be painted into.

    Pixel (i, j) covers x in [min_x + i * cell, +cell) and z in (max_z - (j + 1) * cell, max_z - j * cell].
    Its centre decides whether a shape covers it.
    """

    def __init__(self, width, height, min_x, max_z, cell, fill=0):
        self.width, self.height = width, height
        self.min_x, self.max_z, self.cell = min_x, max_z, cell
        self.data = bytearray([fill]) * (width * height)
        self._fills = {}

    def to_pixel(self, x, z):
        return (x - self.min_x) / self.cell, (self.max_z - z) / self.cell

    def _fill_bytes(self, value):
        f = self._fills.get(value)
        if f is None:
            f = self._fills[value] = memoryview(bytes([value]) * self.width)
        return f

    def fill_polygon(self, rings, value):
        """Paints a polygon with holes (even-odd rule). `rings` are lists of (x, z)."""
        w, h = self.width, self.height
        edges = []
        top, bottom = math.inf, -math.inf
        for ring in rings:
            pts = [self.to_pixel(x, z) for x, z in ring]
            n = len(pts)
            if n < 3:
                continue
            for k in range(n):
                x0, y0 = pts[k]
                x1, y1 = pts[(k + 1) % n]
                if y0 == y1:
                    continue
                if y0 > y1:
                    x0, y0, x1, y1 = x1, y1, x0, y0
                edges.append((y0, y1, x0, (x1 - x0) / (y1 - y0)))
                top, bottom = min(top, y0), max(bottom, y1)
        if not edges:
            return
        edges.sort()
        fill = self._fill_bytes(value)
        data = self.data
        j0 = max(0, math.ceil(top - 0.5))
        j1 = min(h - 1, math.ceil(bottom - 0.5) - 1)
        active = []
        ei, ne = 0, len(edges)
        for j in range(j0, j1 + 1):
            y = j + 0.5
            while ei < ne and edges[ei][0] <= y:
                active.append(edges[ei])
                ei += 1
            active = [e for e in active if e[1] > y]
            if len(active) < 2:
                continue
            xs = sorted(e[2] + (y - e[0]) * e[3] for e in active)
            base = j * w
            for k in range(0, len(xs) - 1, 2):
                a = max(0, math.ceil(xs[k] - 0.5))
                b = min(w, math.ceil(xs[k + 1] - 0.5))
                if b > a:
                    data[base + a:base + b] = fill[:b - a]

    def stroke(self, points, width_m, value):
        """Paints a polyline of the given width (metres) with rounded joins and ends."""
        if not points:
            return
        half = max(width_m, self.cell) / 2.0
        if len(points) == 1:
            self._disc(points[0], half, value)
            return
        for k in range(len(points) - 1):
            (x0, z0), (x1, z1) = points[k], points[k + 1]
            dx, dz = x1 - x0, z1 - z0
            length = math.hypot(dx, dz)
            if length < 1e-9:
                continue
            nx, nz = -dz / length * half, dx / length * half
            self.fill_polygon([[(x0 + nx, z0 + nz), (x1 + nx, z1 + nz), (x1 - nx, z1 - nz), (x0 - nx, z0 - nz)]], value)
        if half >= self.cell:  # round the joints so a bend leaves no wedge
            for p in points:
                self._disc(p, half, value)

    def _disc(self, centre, radius, value, sides=10):
        cx, cz = centre
        ring = [(cx + radius * math.cos(2 * math.pi * k / sides), cz + radius * math.sin(2 * math.pi * k / sides))
                for k in range(sides)]
        self.fill_polygon([ring], value)

    def count(self, value):
        return self.data.count(value)
