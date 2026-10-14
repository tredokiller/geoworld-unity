"""Geodesy and planar geometry helpers (standard library only)."""
import math

WGS84_A = 6378137.0
WGS84_F = 1.0 / 298.257223563
WGS84_E2 = WGS84_F * (2.0 - WGS84_F)


def _to_ecef(lat, lon):
    p, l = math.radians(lat), math.radians(lon)
    sp, cp = math.sin(p), math.cos(p)
    n = WGS84_A / math.sqrt(1.0 - WGS84_E2 * sp * sp)
    return n * cp * math.cos(l), n * cp * math.sin(l), n * (1.0 - WGS84_E2) * sp


class EnuProjection:
    """Local East-North tangent plane at an origin on the WGS84 ellipsoid.

    forward() returns metres east / north of the origin. Distortion stays below
    a few centimetres within ~30 km of the origin. The Unity importer
    (GeoProjection.cs) implements exactly the same maths.
    """

    def __init__(self, lat0, lon0):
        self.lat0 = lat0
        self.lon0 = lon0
        self._x0, self._y0, self._z0 = _to_ecef(lat0, lon0)
        p, l = math.radians(lat0), math.radians(lon0)
        self._sp, self._cp = math.sin(p), math.cos(p)
        self._sl, self._cl = math.sin(l), math.cos(l)

    def forward(self, lat, lon):
        x, y, z = _to_ecef(lat, lon)
        dx, dy, dz = x - self._x0, y - self._y0, z - self._z0
        east = -self._sl * dx + self._cl * dy
        north = -self._sp * self._cl * dx - self._sp * self._sl * dy + self._cp * dz
        return east, north

    def inverse(self, east, north):
        """Lat/lon of a local point, by fixed-point iteration on forward()."""
        lat, lon = self.lat0, self.lon0
        for _ in range(6):
            fx, fy = self.forward(lat, lon)
            m_lat, m_lon = metres_per_degree(lat)
            lat += (north - fy) / m_lat
            lon += (east - fx) / m_lon
        return lat, lon


def metres_per_degree(lat):
    """(metres per degree of latitude, metres per degree of longitude) on WGS84."""
    p = math.radians(lat)
    s2 = math.sin(p) ** 2
    w = math.sqrt(1.0 - WGS84_E2 * s2)
    m = WGS84_A * (1.0 - WGS84_E2) / (w ** 3)
    n = WGS84_A / w
    return math.radians(1) * m, math.radians(1) * n * math.cos(p)


# --- planar geometry on rings: lists of (x, y) tuples, not closed ------------

def signed_area(ring):
    """Shoelace area; positive for counter-clockwise rings."""
    a = 0.0
    n = len(ring)
    for i in range(n):
        x1, y1 = ring[i]
        x2, y2 = ring[(i + 1) % n]
        a += x1 * y2 - x2 * y1
    return a * 0.5


def centroid(ring):
    """Area centroid; falls back to the vertex average for degenerate rings."""
    a = signed_area(ring)
    n = len(ring)
    if abs(a) < 1e-9:
        return sum(p[0] for p in ring) / n, sum(p[1] for p in ring) / n
    cx = cy = 0.0
    for i in range(n):
        x1, y1 = ring[i]
        x2, y2 = ring[(i + 1) % n]
        f = x1 * y2 - x2 * y1
        cx += (x1 + x2) * f
        cy += (y1 + y2) * f
    return cx / (6.0 * a), cy / (6.0 * a)


def point_in_ring(x, y, ring):
    inside = False
    n = len(ring)
    j = n - 1
    for i in range(n):
        xi, yi = ring[i]
        xj, yj = ring[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def bbox(ring):
    xs = [p[0] for p in ring]
    ys = [p[1] for p in ring]
    return min(xs), min(ys), max(xs), max(ys)


def convex_hull(points):
    """Andrew's monotone chain; returns a counter-clockwise hull."""
    pts = sorted(set(points))
    if len(pts) <= 2:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def min_area_rect(points):
    """Minimum-area oriented bounding box.

    Returns (cx, cy, width, length, bearing) where width <= length and bearing
    is the direction of the long side in degrees clockwise from north
    (+y), normalised to [0, 180).
    """
    hull = convex_hull(points)
    if len(hull) < 3:
        xs = [p[0] for p in points]
        ys = [p[1] for p in points]
        return (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, 0.0, 0.0, 0.0

    best = None
    n = len(hull)
    for i in range(n):
        x1, y1 = hull[i]
        x2, y2 = hull[(i + 1) % n]
        ex, ey = x2 - x1, y2 - y1
        elen = math.hypot(ex, ey)
        if elen < 1e-9:
            continue
        ux, uy = ex / elen, ey / elen
        vx, vy = -uy, ux
        us = [px * ux + py * uy for px, py in hull]
        vs = [px * vx + py * vy for px, py in hull]
        umin, umax, vmin, vmax = min(us), max(us), min(vs), max(vs)
        area = (umax - umin) * (vmax - vmin)
        if best is None or area < best[0]:
            best = (area, ux, uy, vx, vy, umin, umax, vmin, vmax)

    _, ux, uy, vx, vy, umin, umax, vmin, vmax = best
    cu, cv = (umin + umax) / 2, (vmin + vmax) / 2
    cx, cy = cu * ux + cv * vx, cu * uy + cv * vy
    eu, ev = umax - umin, vmax - vmin
    if eu >= ev:
        length, width, dx, dy = eu, ev, ux, uy
    else:
        length, width, dx, dy = ev, eu, vx, vy
    bearing = math.degrees(math.atan2(dx, dy)) % 180.0
    return cx, cy, width, length, bearing


def clean_ring(ring, tolerance=0.05):
    """Drops the closing point, duplicate vertices and near-collinear vertices."""
    pts = list(ring)
    if len(pts) > 1 and pts[0] == pts[-1]:
        pts.pop()
    out = []
    for p in pts:
        if not out or math.hypot(p[0] - out[-1][0], p[1] - out[-1][1]) > 1e-3:
            out.append(p)
    if len(out) > 1 and math.hypot(out[0][0] - out[-1][0], out[0][1] - out[-1][1]) <= 1e-3:
        out.pop()

    changed = True
    while changed and len(out) > 3:
        changed = False
        for i in range(len(out)):
            a, b, c = out[i - 1], out[i], out[(i + 1) % len(out)]
            base = math.hypot(c[0] - a[0], c[1] - a[1])
            if base < 1e-9:
                continue
            dist = abs((c[0] - a[0]) * (a[1] - b[1]) - (a[0] - b[0]) * (c[1] - a[1])) / base
            if dist < tolerance:
                out.pop(i)
                changed = True
                break
    return out


def translate_ring(ring, dx, dy):
    return [(x + dx, y + dy) for x, y in ring]


def segment_distance(px, py, ax, ay, bx, by):
    """Distance from (px, py) to the segment a-b."""
    dx, dy = bx - ax, by - ay
    length2 = dx * dx + dy * dy
    t = 0.0 if length2 < 1e-18 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / length2))
    return math.hypot(px - ax - t * dx, py - ay - t * dy)


def clip_half_plane(ring, nx, ny, d):
    """The part of a ring where nx * x + ny * y >= d (Sutherland-Hodgman against one line).

    Works on concave rings too; where a concave ring is cut into separate pieces they come back joined
    along the line, which clean_ring and the area tell apart from a real outline.
    """
    out = []
    n = len(ring)
    for i in range(n):
        ax, ay = ring[i - 1]
        bx, by = ring[i]
        sa, sb = nx * ax + ny * ay - d, nx * bx + ny * by - d
        if (sa >= 0) != (sb >= 0):
            t = sa / (sa - sb)
            out.append((ax + t * (bx - ax), ay + t * (by - ay)))
        if sb >= 0:
            out.append((bx, by))
    return out


def intersection_area(a, b):
    """Area shared by two simple rings, both counter-clockwise.

    The boundary of the intersection is made of the parts of each ring's edges that lie inside the other
    ring; summing the shoelace terms of those parts gives its area. A part lying on an edge of the other
    ring counts once when the two edges run the same way (a wall both outlines have) and not at all when
    they run opposite ways (two buildings that only touch).
    """
    def inside_parts(ring, other, count_shared):
        total = 0.0
        n, m = len(ring), len(other)
        for i in range(n):
            ax, ay = ring[i]
            bx, by = ring[(i + 1) % n]
            dx, dy = bx - ax, by - ay
            dd = dx * dx + dy * dy
            if dd < 1e-18:
                continue
            ts = [0.0, 1.0]
            shared = []   # (t from, t to, same direction) of the other ring's edges lying along this one
            for j in range(m):
                cx, cy = other[j]
                ex, ey = other[(j + 1) % m]
                fx, fy = ex - cx, ey - cy
                den = dx * fy - dy * fx
                if abs(den) <= 1e-9 * math.sqrt(dd * (fx * fx + fy * fy)):
                    # parallel: shared only when on the same line
                    if abs(dx * (cy - ay) - dy * (cx - ax)) <= 1e-6 * math.sqrt(dd):
                        tc = ((cx - ax) * dx + (cy - ay) * dy) / dd
                        te = ((ex - ax) * dx + (ey - ay) * dy) / dd
                        ts.extend(t for t in (tc, te) if 0.0 < t < 1.0)
                        shared.append((min(tc, te), max(tc, te), dx * fx + dy * fy > 0))
                    continue
                t = ((cx - ax) * fy - (cy - ay) * fx) / den
                u = ((cx - ax) * dy - (cy - ay) * dx) / den
                if 0.0 < t < 1.0 and -1e-9 <= u <= 1.0 + 1e-9:
                    ts.append(t)
            ts.sort()
            for t0, t1 in zip(ts, ts[1:]):
                if t1 - t0 < 1e-12:
                    continue
                tm = (t0 + t1) / 2
                on = next((same for lo, hi, same in shared if lo - 1e-9 <= tm <= hi + 1e-9), None)
                if on is None:
                    keep = point_in_ring(ax + tm * dx, ay + tm * dy, other)
                else:
                    keep = on and count_shared
                if keep:
                    x0, y0 = ax + t0 * dx, ay + t0 * dy
                    x1, y1 = ax + t1 * dx, ay + t1 * dy
                    total += x0 * y1 - x1 * y0
        return total

    return max(0.0, 0.5 * (inside_parts(a, b, True) + inside_parts(b, a, False)))


class GridIndex:
    """Uniform grid over ring bounding boxes for fast neighbourhood queries."""

    def __init__(self, cell=50.0):
        self.cell = cell
        self.cells = {}

    def insert(self, item, box):
        c = self.cell
        for ix in range(int(math.floor(box[0] / c)), int(math.floor(box[2] / c)) + 1):
            for iy in range(int(math.floor(box[1] / c)), int(math.floor(box[3] / c)) + 1):
                self.cells.setdefault((ix, iy), []).append(item)

    def query_box(self, box):
        c = self.cell
        found = {}
        for ix in range(int(math.floor(box[0] / c)), int(math.floor(box[2] / c)) + 1):
            for iy in range(int(math.floor(box[1] / c)), int(math.floor(box[3] / c)) + 1):
                for item in self.cells.get((ix, iy), ()):
                    found[id(item)] = item
        return found.values()


# --- lines and clipping ---------------------------------------------------------

def simplify(points, tolerance):
    """Douglas-Peucker on an open polyline; the end points always stay."""
    n = len(points)
    if n < 3 or tolerance <= 0:
        return list(points)
    keep = [False] * n
    keep[0] = keep[-1] = True
    stack = [(0, n - 1)]
    while stack:
        a, b = stack.pop()
        ax, ay = points[a]
        bx, by = points[b]
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        far, far_d = -1, tolerance
        for i in range(a + 1, b):
            px, py = points[i]
            if length < 1e-12:
                d = math.hypot(px - ax, py - ay)
            else:
                d = abs(dx * (ay - py) - (ax - px) * dy) / length
            if d > far_d:
                far, far_d = i, d
        if far >= 0:
            keep[far] = True
            stack.append((a, far))
            stack.append((far, b))
    return [p for p, k in zip(points, keep) if k]


def simplify_ring(ring, tolerance):
    """Douglas-Peucker on a closed ring (not repeating its first point). Never returns fewer than 3 points."""
    if len(ring) <= 3:
        return list(ring)
    out = simplify(list(ring) + [ring[0]], tolerance)[:-1]
    return out if len(out) >= 3 else list(ring)


def clip_polyline(points, rect):
    """Parts of a polyline inside rect = (min_x, min_y, max_x, max_y), as a list of polylines."""
    x0, y0, x1, y1 = rect
    runs, current = [], []
    for k in range(len(points) - 1):
        (ax, ay), (bx, by) = points[k], points[k + 1]
        dx, dy = bx - ax, by - ay
        t0, t1 = 0.0, 1.0
        ok = True
        for p, q in ((-dx, ax - x0), (dx, x1 - ax), (-dy, ay - y0), (dy, y1 - ay)):
            if p == 0:
                if q < 0:
                    ok = False
                    break
            else:
                t = q / p
                if p < 0:
                    if t > t1:
                        ok = False
                        break
                    t0 = max(t0, t)
                else:
                    if t < t0:
                        ok = False
                        break
                    t1 = min(t1, t)
        if not ok:
            if len(current) > 1:
                runs.append(current)
            current = []
            continue
        start = (ax + t0 * dx, ay + t0 * dy)
        end = (ax + t1 * dx, ay + t1 * dy)
        if current and t0 == 0.0:
            current.append(end)
        else:
            if len(current) > 1:
                runs.append(current)
            current = [start, end]
        if t1 < 1.0:
            runs.append(current)
            current = []
    if len(current) > 1:
        runs.append(current)
    return runs


def boxes_overlap(a, b):
    return a[0] <= b[2] and b[0] <= a[2] and a[1] <= b[3] and b[1] <= a[3]
