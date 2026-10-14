"""Minimal Cloud Optimized GeoTIFF reader (standard library only).

Fetches only the tiles a window needs, through HTTP range requests, and keeps
them on disk so a repeated extraction costs no network. Handles tiled,
DEFLATE-compressed, single-band rasters with no predictor or the floating-point
predictor, which is what ESA WorldCover and the Copernicus DEM use.
"""
import hashlib
import json
import os
import struct
import threading
import time
import urllib.error
import urllib.request
import zlib
from array import array
from concurrent.futures import ThreadPoolExecutor
from itertools import accumulate

from .common import Cancelled, USER_AGENT

HEADER_BYTES = 32768
FETCH_THREADS = 6   # tiles of one window fetched at once
CHUNK = 512 * 1024  # a larger tile is split into parts of this size, fetched in parallel
TIMEOUT = 60

_pool = None
_pool_lock = threading.Lock()


def _chunk_pool():
    global _pool
    with _pool_lock:
        if _pool is None:
            _pool = ThreadPoolExecutor(8)
        return _pool

_TAG_TYPES = {1: ("B", 1), 2: ("c", 1), 3: ("H", 2), 4: ("I", 4), 6: ("b", 1), 7: ("B", 1),
              8: ("h", 2), 9: ("i", 4), 11: ("f", 4), 12: ("d", 8)}

# (SampleFormat, bits per sample) -> array typecode
_TYPECODES = {(1, 8): "B", (1, 16): "H", (1, 32): "I", (2, 16): "h", (2, 32): "i", (3, 32): "f", (3, 64): "d"}


class TileMissing(Exception):
    """The file does not exist on the server (for example, a DEM tile over open sea)."""


def _get(url, start, end, retries=3):
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Range": f"bytes={start}-{end}"})
    for attempt in range(retries):
        try:
            with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
                return resp.read()
        except urllib.error.HTTPError as e:
            if e.code in (403, 404):
                raise TileMissing(url) from e
            if attempt + 1 == retries:
                raise
        except (urllib.error.URLError, TimeoutError, OSError):
            if attempt + 1 == retries:
                raise
        time.sleep(1.5 * (attempt + 1))


def _parse_ifd(url, head):
    """Reads the first IFD of a classic TIFF; returns {tag: tuple of values}."""
    bo = "<" if head[:2] == b"II" else ">"
    magic = struct.unpack(bo + "H", head[2:4])[0]
    if magic != 42:
        raise ValueError("only classic TIFF files are supported")
    off = struct.unpack(bo + "I", head[4:8])[0]
    n = struct.unpack(bo + "H", head[off:off + 2])[0]
    tags = {}
    for i in range(n):
        e = head[off + 2 + i * 12: off + 14 + i * 12]
        tag, typ, cnt = struct.unpack(bo + "HHI", e[:8])
        fmt, size = _TAG_TYPES.get(typ, ("B", 1))
        total = size * cnt
        if total <= 4:
            raw = e[8:8 + total]
        else:
            o = struct.unpack(bo + "I", e[8:12])[0]
            raw = head[o:o + total] if o + total <= len(head) else _get(url, o, o + total - 1)
        if typ == 2:
            tags[tag] = raw.decode("latin1").rstrip("\0")
        else:
            tags[tag] = struct.unpack(bo + fmt * cnt, raw)
    return tags, bo


def _undo_float_predictor(buf, tile_w, tile_h, bps):
    """TIFF predictor 3: byte-wise differencing, then byte planes stored most significant first."""
    rowlen = tile_w * bps
    out = bytearray(len(buf))
    low_byte = (255).__and__
    for r in range(tile_h):
        row = bytes(map(low_byte, accumulate(buf[r * rowlen:(r + 1) * rowlen])))
        o = bytearray(rowlen)
        for b in range(bps):  # little-endian output: byte b comes from plane bps-1-b
            o[b::bps] = row[(bps - 1 - b) * tile_w:(bps - b) * tile_w]
        out[r * rowlen:(r + 1) * rowlen] = o
    return out


class Cog:
    """A remote tiled GeoTIFF. Pixel (0, 0) is the top-left corner at (x0, y0)."""

    def __init__(self, url, cache_dir):
        self.url = url
        digest = hashlib.sha1(url.encode()).hexdigest()[:10]
        self.dir = cache_dir / "cog" / f"{url.rsplit('/', 1)[-1][:60]}_{digest}"
        self.dir.mkdir(parents=True, exist_ok=True)
        meta_path = self.dir / "header.json"
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            meta = self._read_header()
            meta_path.write_text(json.dumps(meta), encoding="utf-8")
        self.__dict__.update(meta)

    def _read_header(self):
        head = _get(self.url, 0, HEADER_BYTES - 1)
        tags, _ = _parse_ifd(self.url, head)
        for need in (256, 257, 258, 259, 322, 323, 324, 325, 33550, 33922):
            if need not in tags:
                raise ValueError(f"{self.url} is not a tiled, georeferenced GeoTIFF (missing tag {need})")
        compression = tags[259][0]
        if compression not in (8, 32946):
            raise ValueError(f"unsupported TIFF compression {compression}")
        predictor = tags.get(317, (1,))[0]
        if predictor not in (1, 3):
            raise ValueError(f"unsupported TIFF predictor {predictor}")
        # GeoKeyDirectory: key 1025 (GTRasterTypeGeoKey) is 1 for pixel-is-area, 2 for pixel-is-point.
        raster_type = 1
        keys = tags.get(34735, ())
        for k in range(4, len(keys) - 3, 4):
            if keys[k] == 1025:
                raster_type = keys[k + 3]
        scale, tie = tags[33550], tags[33922]
        return {
            "width": tags[256][0], "height": tags[257][0],
            "bits": tags[258][0], "sample_format": tags.get(339, (1,))[0],
            "tile_w": tags[322][0], "tile_h": tags[323][0],
            "predictor": predictor,
            "offsets": list(tags[324]), "counts": list(tags[325]),
            "sx": scale[0], "sy": scale[1], "x0": tie[3] - tie[0] * scale[0], "y0": tie[4] + tie[1] * scale[1],
            "pixel_is_point": raster_type == 2,
            "nodata": float(tags[42113]) if 42113 in tags and str(tags[42113]).strip() else None,
        }

    # -- geometry ---------------------------------------------------------

    def col_of(self, lon):
        """Fractional column of a longitude, where integer + 0.5 is a pixel centre."""
        c = (lon - self.x0) / self.sx
        return c + 0.5 if self.pixel_is_point else c

    def row_of(self, lat):
        r = (self.y0 - lat) / self.sy
        return r + 0.5 if self.pixel_is_point else r

    # -- tiles ------------------------------------------------------------

    @property
    def typecode(self):
        return _TYPECODES[(self.sample_format, self.bits)]

    def _tiles_across(self):
        return -(-self.width // self.tile_w)

    def _fetch(self, index, report=None):
        path = self.dir / f"{index}.z"
        if path.exists():
            return path.read_bytes()
        off, cnt = self.offsets[index], self.counts[index]
        if cnt <= CHUNK * 3 // 2:
            data = _get(self.url, off, off + cnt - 1) if cnt else b""
            if report:
                report(len(data))
        else:
            # A big block over a slow link: several connections at once are much faster than one.
            def part(o):
                chunk = _get(self.url, off + o, off + min(o + CHUNK, cnt) - 1)
                if report:
                    report(len(chunk))
                return chunk
            data = b"".join(_chunk_pool().map(part, range(0, cnt, CHUNK)))
        tmp = path.with_suffix(f".tmp{os.getpid()}_{threading.get_ident()}")
        tmp.write_bytes(data)
        os.replace(tmp, path)
        return data

    def _decode(self, index, report=None):
        data = self._fetch(index, report)
        bps = self.bits // 8
        size = self.tile_w * self.tile_h * bps
        raw = zlib.decompress(data) if data else bytes(size)
        if self.predictor == 3:
            raw = _undo_float_predictor(raw, self.tile_w, self.tile_h, bps)
        return raw

    def missing_bytes(self, col0, row0, col1, row1):
        """Compressed size of the tiles of a window that are not cached yet."""
        col0, row0, col1, row1 = max(0, col0), max(0, row0), min(self.width, col1), min(self.height, row1)
        if col1 <= col0 or row1 <= row0:
            return 0
        across = self._tiles_across()
        return sum(self.counts[ty * across + tx]
                   for ty in range(row0 // self.tile_h, (row1 - 1) // self.tile_h + 1)
                   for tx in range(col0 // self.tile_w, (col1 - 1) // self.tile_w + 1)
                   if not (self.dir / f"{ty * across + tx}.z").exists())

    def read_window(self, col0, row0, col1, row1, is_cancelled=lambda: False, report=None):
        """Pixels of [col0, col1) x [row0, row1), clipped to the image.

        `report(nbytes)` is called, from worker threads, as downloaded data arrives.
        Returns (array, col0, row0, width, height) with the clipped window.
        """
        col0, row0 = max(0, col0), max(0, row0)
        col1, row1 = min(self.width, col1), min(self.height, row1)
        w, h = col1 - col0, row1 - row0
        if w <= 0 or h <= 0:
            return array(self.typecode), col0, row0, 0, 0
        across = self._tiles_across()
        needed = [(ty * across + tx, tx, ty)
                  for ty in range(row0 // self.tile_h, (row1 - 1) // self.tile_h + 1)
                  for tx in range(col0 // self.tile_w, (col1 - 1) // self.tile_w + 1)]

        def prefetch(item):
            if is_cancelled():
                raise Cancelled()
            self._fetch(item[0], report)
        with ThreadPoolExecutor(FETCH_THREADS) as pool:
            list(pool.map(prefetch, needed))

        out = array(self.typecode, bytes(w * h * array(self.typecode).itemsize))
        for index, tx, ty in needed:
            if is_cancelled():
                raise Cancelled()
            tile = array(self.typecode)
            tile.frombytes(bytes(self._decode(index)))
            tc0, tr0 = tx * self.tile_w, ty * self.tile_h
            a0, a1 = max(col0, tc0), min(col1, tc0 + self.tile_w)
            for r in range(max(row0, tr0), min(row1, tr0 + self.tile_h)):
                src = (r - tr0) * self.tile_w + (a0 - tc0)
                dst = (r - row0) * w + (a0 - col0)
                out[dst:dst + (a1 - a0)] = tile[src:src + (a1 - a0)]
        return out, col0, row0, w, h
